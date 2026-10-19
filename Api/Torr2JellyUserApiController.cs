using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Torr2Jelly.Api.Dtos;
using Jellyfin.Plugin.Torr2Jelly.Configuration;
using Jellyfin.Plugin.Torr2Jelly.Engine;
using Jellyfin.Plugin.Torr2Jelly.Search;
using Jellyfin.Plugin.Torr2Jelly.Search.Models;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torr2Jelly.Api;

/// <summary>
/// The part of the API that any logged-in user may use (searching and adding releases from the Jellyfin search
/// page). It is a separate controller because ASP.NET Core requires every authorization attribute of a class
/// AND of its action to pass: an action in <see cref="Torr2JellyApiController"/> could not relax that
/// controller's administrator policy. Anything that is not meant for every user belongs there, not here.
/// </summary>
[ApiController]
[Route("Plugins/Torr2Jelly")]
[Authorize]
public class Torr2JellyUserApiController : ControllerBase
{
    private const int MaxQueryLength = 200;
    private const int MaxTitleLength = 300;

    private static readonly Regex ImdbIdRegex = new(@"^tt\d{1,10}$", RegexOptions.Compiled);

    // VirtualFolderInfo.CollectionType is a different enum type in Jellyfin 10.11 than in the 10.9 this plugin is
    // built against; reading it by reflection avoids a MissingMethodException there.
    private static readonly PropertyInfo? CollectionTypeProperty = typeof(VirtualFolderInfo).GetProperty("CollectionType");

    private readonly ILogger<Torr2JellyUserApiController> _logger;
    private readonly TorrClientService _torrClient;
    private readonly StrmEngine _strmEngine;
    private readonly TorrentSearchService _searchService;
    private readonly ILibraryManager _libraryManager;
    private readonly TorrentLinkResolver _linkResolver;

    public Torr2JellyUserApiController(
        ILogger<Torr2JellyUserApiController> logger,
        TorrClientService torrClient,
        StrmEngine strmEngine,
        TorrentSearchService searchService,
        ILibraryManager libraryManager,
        TorrentLinkResolver linkResolver)
    {
        _logger = logger;
        _torrClient = torrClient;
        _strmEngine = strmEngine;
        _searchService = searchService;
        _libraryManager = libraryManager;
        _linkResolver = linkResolver;
    }

    private bool IsAdministrator => User.IsInRole(ApiPolicies.AdministratorRole);

    private static bool IsAllowedForRegularUser(string? link, PluginConfiguration config)
    {
        if (!TorrentLinkResolver.TryNormalize(link, out var normalized, out var isHttp, out _))
        {
            return true; // invalid links are rejected by the resolver with a proper message
        }

        if (!isHttp)
        {
            return true;
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var target))
        {
            return false;
        }

        foreach (var baseUrl in new[] { config.EnableProwlarr ? config.ProwlarrUrl : null, config.EnableTorznab ? config.TorznabUrl : null })
        {
            if (!string.IsNullOrWhiteSpace(baseUrl)
                && Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var indexer)
                && string.Equals(indexer.Host, target.Host, StringComparison.OrdinalIgnoreCase)
                && indexer.Port == target.Port)
            {
                return true;
            }
        }

        return false;
    }

    [HttpGet("Search")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<TorrentSearchResult>>> Search(
        [FromQuery] string? query = null,
        [FromQuery] string? category = null,
        [FromQuery] bool? onlyGold = null,
        [FromQuery] bool? onlyTags = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Ok(new List<TorrentSearchResult>());
        }

        query = query.Trim();
        if (query.Length > MaxQueryLength)
        {
            query = query.Substring(0, MaxQueryLength);
        }

        var filter = onlyTags ?? onlyGold;
        var results = await _searchService.SearchAsync(query, category, filter, isUserAdmin: IsAdministrator, cancellationToken);
        return Ok(results);
    }

    /// <summary>
    /// Libraries a release can be added to: movie and TV libraries (and mixed ones). The folders are only shown
    /// to administrators.
    /// </summary>
    [HttpGet("Libraries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<ServerLibraryDto>> GetLibraries()
    {
        var isAdmin = IsAdministrator;
        var result = new List<ServerLibraryDto>();

        foreach (var vf in _libraryManager.GetVirtualFolders())
        {
            var collType = GetCollectionType(vf);
            var normalized = collType.ToLowerInvariant();
            var isShow = normalized == "tvshows" || normalized == "series";
            var isMovie = normalized == "movies";
            var isMixed = normalized.Length == 0 || normalized == "mixed";
            if (!isShow && !isMovie && !isMixed)
            {
                // music, books, photos, home videos, ...: not a target for torrent releases
                continue;
            }

            var locations = vf.Locations ?? Array.Empty<string>();
            result.Add(new ServerLibraryDto
            {
                Name = vf.Name,
                CollectionType = collType,
                Type = isShow ? "show" : "movie",
                Locations = isAdmin ? locations.ToList() : new List<string>(),
                PrimaryLocation = isAdmin ? locations.FirstOrDefault() ?? string.Empty : string.Empty
            });
        }

        return Ok(result);
    }

    private static string GetCollectionType(VirtualFolderInfo folder)
    {
        try
        {
            return CollectionTypeProperty?.GetValue(folder)?.ToString() ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    [HttpPost("Torrents")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<StrmSyncResult>> AddTorrent(
        [FromBody] AddTorrentRequestDto req,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var isAdmin = IsAdministrator;

        // The server fetches http(s) links itself (to follow redirects to magnet links), so a regular user may only
        // hand it links of the configured indexers; magnet links and bare hashes involve no request.
        if (!isAdmin && !IsAllowedForRegularUser(req.Link, config))
        {
            return BadRequest(new { error = "Only magnet links, info-hashes or links from the configured indexers can be added." });
        }

        var resolution = await _linkResolver.ResolveAsync(req.Link, cancellationToken);
        if (!resolution.Success)
        {
            return BadRequest(new { error = resolution.Error });
        }

        // Only a configured category is passed on; anything else lets the engine route by what the release is.
        var category = config.Categories
            .FirstOrDefault(c => c != null && c.Name.Equals(req.Category?.Trim(), StringComparison.OrdinalIgnoreCase))?.Name;

        var title = string.IsNullOrWhiteSpace(req.Title) ? null : req.Title.Trim();
        if (title != null && title.Length > MaxTitleLength)
        {
            title = title.Substring(0, MaxTitleLength);
        }

        // Posters are downloaded by the server too; Jellyfin fetches artwork from its metadata providers anyway.
        var posterUrl = isAdmin ? TorrentLinkResolver.SanitizePosterUrl(req.PosterUrl) : null;
        var tmdbId = req.TmdbId > 0 ? req.TmdbId : null;
        var imdbId = !string.IsNullOrWhiteSpace(req.ImdbId) && ImdbIdRegex.IsMatch(req.ImdbId.Trim()) ? req.ImdbId.Trim() : null;

        try
        {
            var torrent = await _torrClient.AddTorrentAsync(
                link: resolution.Link,
                title: title,
                poster: posterUrl,
                category: category,
                saveToDb: true,
                cancellationToken: cancellationToken);

            if (torrent == null)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new { error = "TorrServer did not accept the torrent." });
            }

            if (category != null)
            {
                await _torrClient.SetTorrentCategoryAsync(torrent.Hash, category, torrent.Title, torrent.Poster, cancellationToken);
            }

            if (req.SyncNow)
            {
                var syncResult = await _strmEngine.SyncTorrentAsync(
                    torrent: torrent,
                    category: category,
                    customTitle: title,
                    posterUrl: posterUrl,
                    tmdbId: tmdbId,
                    imdbId: imdbId,
                    forceCategory: category != null,
                    cancellationToken: cancellationToken);

                // The torrent is in TorrServer, but without .strm files it is not in the library: that is a failure.
                // "Metadata not ready" is the exception: the files follow with the next sync, so it stays a 200.
                if (!syncResult.Success &&
                    !string.Equals(syncResult.ErrorMessage, StrmEngine.MetadataNotReadyMessage, StringComparison.Ordinal))
                {
                    return StatusCode(
                        StatusCodes.Status500InternalServerError,
                        new { error = syncResult.ErrorMessage ?? "Failed to write the STRM files; see the Jellyfin log for details." });
                }

                return Ok(IsAdministrator ? syncResult : WithoutServerPaths(syncResult));
            }

            return Ok(new StrmSyncResult
            {
                TorrentHash = torrent.Hash,
                TorrentTitle = torrent.Title,
                Success = true
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding torrent");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to add the torrent; see the Jellyfin log for details." });
        }
    }

    // Ordinary users get the outcome, not the layout of the server's file system.
    private static StrmSyncResult WithoutServerPaths(StrmSyncResult result)
    {
        return new StrmSyncResult
        {
            TorrentHash = result.TorrentHash,
            TorrentTitle = result.TorrentTitle,
            Category = result.Category,
            VideoFilesCount = result.VideoFilesCount,
            Success = result.Success,
            ErrorMessage = result.ErrorMessage
        };
    }
}
