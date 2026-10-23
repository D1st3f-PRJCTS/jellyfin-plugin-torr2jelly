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

        // onlyGold / onlyTags are obsolete and ignored: the FreeleechOnly setting decides what is returned.
        var results = await _searchService.SearchAsync(query, category, null, isUserAdmin: IsAdministrator, cancellationToken);
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

        // Posters are downloaded by the server too; Jellyfin fetches artwork from its metadata providers anyway.
        var posterUrl = isAdmin ? TorrentLinkResolver.SanitizePosterUrl(req.PosterUrl) : null;

        // Everything after the torrent has been obtained is shared with the .torrent file upload (TorrentIntake).
        return await TorrentIntake.AddAsync(
            _torrClient,
            _strmEngine,
            _logger,
            isAdmin,
            (title, poster, category, ct) => _torrClient.AddTorrentAsync(
                link: resolution.Link,
                title: title,
                poster: poster,
                category: category,
                saveToDb: true,
                cancellationToken: ct),
            req.Category,
            req.Title,
            posterUrl,
            req.TmdbId,
            req.ImdbId,
            req.SyncNow,
            cancellationToken);
    }
}
