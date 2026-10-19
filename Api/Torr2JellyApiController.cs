using MediaBrowser.Model.Entities;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Torr2Jelly.Api.Dtos;
using Jellyfin.Plugin.Torr2Jelly.Configuration;
using Jellyfin.Plugin.Torr2Jelly.Daemon;
using Jellyfin.Plugin.Torr2Jelly.Engine;
using Jellyfin.Plugin.Torr2Jelly.Search;
using Jellyfin.Plugin.Torr2Jelly.Search.Models;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torr2Jelly.Api;

[ApiController]
[Route("Plugins/Torr2Jelly")]
public class Torr2JellyApiController : ControllerBase
{
    private readonly ILogger<Torr2JellyApiController> _logger;
    private readonly TorrServerDaemonManager _daemonManager;
    private readonly TorrClientService _torrClient;
    private readonly StrmEngine _strmEngine;
    private readonly TorrentSearchService _searchService;
    private readonly ILibraryManager _libraryManager;

    public Torr2JellyApiController(
        ILogger<Torr2JellyApiController> logger,
        TorrServerDaemonManager daemonManager,
        TorrClientService torrClient,
        StrmEngine strmEngine,
        TorrentSearchService searchService,
        ILibraryManager libraryManager)
    {
        _logger = logger;
        _daemonManager = daemonManager;
        _torrClient = torrClient;
        _strmEngine = strmEngine;
        _searchService = searchService;
        _libraryManager = libraryManager;
    }

    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<PluginStatusDto>> GetStatus(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var torrents = await _torrClient.ListTorrentsAsync(cancellationToken);

        var status = new PluginStatusDto
        {
            IsDaemonRunning = _daemonManager.IsRunning,
            DaemonVersion = _daemonManager.DaemonVersion,
            StatusMessage = _daemonManager.StatusMessage,
            EffectiveUrl = _daemonManager.GetEffectiveUrl(),
            StreamBaseUrl = _daemonManager.GetStreamBaseUrl(),
            ActiveTorrentsCount = torrents.Count,
            CacheSizeMb = config.CacheSizeMb,
            UseDiskCache = config.UseDiskCache,
            DiskCachePath = config.DiskCachePath
        };

        return Ok(status);
    }

    [HttpGet("Libraries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<ServerLibraryDto>> GetLibraries()
    {
        var virtualFolders = _libraryManager.GetVirtualFolders();
        var result = virtualFolders.Select(vf =>
        {
            var collType = vf.CollectionType?.ToString() ?? string.Empty;
            var isShow = collType.Equals("tvshows", StringComparison.OrdinalIgnoreCase) ||
                         collType.Equals("series", StringComparison.OrdinalIgnoreCase);
            return new ServerLibraryDto
            {
                Name = vf.Name,
                CollectionType = collType,
                Type = isShow ? "show" : "movie",
                Locations = vf.Locations.ToList(),
                PrimaryLocation = vf.Locations.FirstOrDefault() ?? string.Empty
            };
        }).ToList();

        return Ok(result);
    }

    [HttpGet("Indexers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<IndexerStatusDto>>> GetIndexers(CancellationToken cancellationToken)
    {
        var indexers = await _searchService.GetAvailableIndexersAsync(cancellationToken);
        return Ok(indexers);
    }

    [HttpGet("Search")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<TorrentSearchResult>>> Search(
        [FromQuery] string query,
        [FromQuery] string? category = null,
        [FromQuery] bool? onlyGold = null,
        [FromQuery] bool? onlyTags = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Ok(new List<TorrentSearchResult>());
        }

        var filter = onlyTags ?? onlyGold;
        var results = await _searchService.SearchAsync(query, category, filter, isUserAdmin: true, cancellationToken);
        return Ok(results);
    }

    [HttpGet("Torrents")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<TorrTorrent>>> GetTorrents(CancellationToken cancellationToken)
    {
        var torrents = await _torrClient.ListTorrentsAsync(cancellationToken);
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        foreach (var t in torrents)
        {
            if (string.IsNullOrWhiteSpace(t.Category))
            {
                foreach (var cat in config.Categories)
                {
                    var catFolder = _strmEngine.ResolveMediaRoot(cat.Folder);
                    if (Directory.Exists(catFolder))
                    {
                        var strms = Directory.GetFiles(catFolder, "*.strm", SearchOption.AllDirectories);
                        var found = strms.Any(s =>
                        {
                            try { return System.IO.File.ReadAllText(s).Contains(t.Hash, StringComparison.OrdinalIgnoreCase); }
                            catch { return false; }
                        });

                        if (found)
                        {
                            t.Category = cat.Name;
                            // Persist in TorrServer in the background
                            _ = _torrClient.SetTorrentCategoryAsync(t.Hash, cat.Name, t.Title, t.Poster, cancellationToken);
                            break;
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(t.Category))
                {
                    var isShow = EpisodeMatcher.IsShowRelease(t.Title, t.FileStats?.Select(f => f.Path) ?? Enumerable.Empty<string>()) || (t.FileStats != null && t.FileStats.Count > 1);
                    var defaultCat = config.Categories.FirstOrDefault(c => c.Type.Equals(isShow ? "show" : "movie", StringComparison.OrdinalIgnoreCase))?.Name
                        ?? config.Categories.FirstOrDefault()?.Name
                        ?? (isShow ? "Shows" : "Movies");
                    t.Category = defaultCat;
                    _ = _torrClient.SetTorrentCategoryAsync(t.Hash, defaultCat, t.Title, t.Poster, cancellationToken);
                }
            }
        }

        return Ok(torrents);
    }

    [HttpPost("Torrents")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<StrmSyncResult>> AddTorrent(
        [FromBody] AddTorrentRequestDto req,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(req.Link))
        {
            return BadRequest(new { error = "Torrent link or magnet is required." });
        }

        try
        {
            var torrent = await _torrClient.AddTorrentAsync(
                link: req.Link,
                title: req.Title,
                poster: req.PosterUrl,
                category: req.Category,
                saveToDb: true,
                cancellationToken: cancellationToken);

            if (torrent == null)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new { error = "Failed to communicate with TorrServer." });
            }

            if (!string.IsNullOrWhiteSpace(req.Category))
            {
                await _torrClient.SetTorrentCategoryAsync(torrent.Hash, req.Category, torrent.Title, torrent.Poster, cancellationToken);
            }

            if (req.SyncNow)
            {
                var syncResult = await _strmEngine.SyncTorrentAsync(
                    torrent: torrent,
                    category: req.Category,
                    customTitle: req.Title,
                    posterUrl: req.PosterUrl,
                    tmdbId: req.TmdbId,
                    imdbId: req.ImdbId,
                    forceCategory: !string.IsNullOrWhiteSpace(req.Category),
                    cancellationToken: cancellationToken);

                return Ok(syncResult);
            }

            return Ok(new StrmSyncResult
            {
                TorrentHash = torrent.Hash,
                TorrentTitle = torrent.Title,
                Success = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding torrent");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = ex.Message });
        }
    }

    [HttpPost("Torrents/{hash}/Recategorize")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<StrmSyncResult>> RecategorizeTorrent(
        [FromRoute] string hash,
        [FromBody] RecategorizeTorrentRequestDto req,
        CancellationToken cancellationToken)
    {
        var torrent = await _torrClient.GetTorrentAsync(hash, cancellationToken);
        if (torrent == null)
        {
            return NotFound(new { error = "Torrent not found in TorrServer" });
        }

        // Delete existing .strm folders for this hash
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        foreach (var cat in config.Categories)
        {
            var catFolder = _strmEngine.ResolveMediaRoot(cat.Folder);
            if (Directory.Exists(catFolder))
            {
                foreach (var subDir in Directory.GetDirectories(catFolder))
                {
                    var strms = Directory.GetFiles(subDir, "*.strm", SearchOption.AllDirectories);
                    if (strms.Any(s =>
                    {
                        try { return System.IO.File.ReadAllText(s).Contains(hash, StringComparison.OrdinalIgnoreCase); }
                        catch { return false; }
                    }))
                    {
                        _strmEngine.DeleteTorrentStrm(subDir);
                    }
                }
            }
        }

        // Persist category in TorrServer
        await _torrClient.SetTorrentCategoryAsync(hash, req.Category, torrent.Title, torrent.Poster, cancellationToken);
        torrent.Category = req.Category;

        // Re-sync with new category, forcing user's category selection
        var syncResult = await _strmEngine.SyncTorrentAsync(
            torrent: torrent,
            category: req.Category,
            customTitle: req.CustomTitle,
            forceCategory: true,
            cancellationToken: cancellationToken);

        return Ok(syncResult);
    }

    [HttpDelete("Torrents/{hash}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> DeleteTorrent(
        [FromRoute] string hash,
        [FromQuery] bool deleteStrm = true,
        CancellationToken cancellationToken = default)
    {
        var success = await _torrClient.RemoveTorrentAsync(hash, cancellationToken);
        if (deleteStrm)
        {
            // Scan and delete corresponding folder if exists
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            foreach (var cat in config.Categories)
            {
                var catFolder = _strmEngine.ResolveMediaRoot(cat.Folder);
                if (Directory.Exists(catFolder))
                {
                    foreach (var subDir in Directory.GetDirectories(catFolder))
                    {
                        var strms = Directory.GetFiles(subDir, "*.strm", SearchOption.AllDirectories);
                        var matchFound = false;
                        foreach (var strm in strms)
                        {
                            try
                            {
                                var content = await System.IO.File.ReadAllTextAsync(strm, cancellationToken);
                                if (content.Contains(hash, StringComparison.OrdinalIgnoreCase))
                                {
                                    matchFound = true;
                                    break;
                                }
                            }
                            catch { }
                        }

                        if (matchFound)
                        {
                            _strmEngine.DeleteTorrentStrm(subDir);
                        }
                    }
                }
            }
        }

        return Ok(new { success });
    }

    [HttpGet("Torrents/{hash}/Files")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetTorrentFiles(
        [FromRoute] string hash,
        CancellationToken cancellationToken)
    {
        var torrent = await _torrClient.GetTorrentAsync(hash, cancellationToken);
        if (torrent == null)
        {
            return NotFound(new { error = "Torrent not found." });
        }

        return Ok(torrent.FileStats ?? new List<TorrTorrentFile>());
    }

    [HttpGet("Settings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PluginConfiguration> GetSettings()
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        config.DeduplicateCategories();
        config.NormalizeTrackerRules();
        return Ok(config);
    }

    [HttpPost("Settings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> SaveSettings(
        [FromBody] PluginConfiguration newConfig,
        CancellationToken cancellationToken)
    {
        if (Plugin.Instance == null)
        {
            return StatusCode(StatusCodes.Status500InternalServerError);
        }

        newConfig.DeduplicateCategories();
        newConfig.NormalizeTrackerRules();
        Plugin.Instance.UpdateConfiguration(newConfig);
        await _daemonManager.ConfigureDaemonSettingsAsync(cancellationToken);

        return Ok(new { success = true });
    }

    [HttpPost("Daemon/Restart")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> RestartDaemon(CancellationToken cancellationToken)
    {
        await _daemonManager.RestartAsync(cancellationToken);
        return Ok(new { success = true, status = _daemonManager.StatusMessage });
    }

    [HttpGet("search.js")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public ActionResult GetSearchScript()
    {
        var assembly = typeof(Plugin).Assembly;
        var resourceName = $"{typeof(Plugin).Namespace}.Web.torr2jellysearch.js";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            return NotFound("/* Search script resource not found */");
        }

        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();
        return Content(content, "application/javascript");
    }

    [HttpPost("Library/Scan")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> TriggerScan(CancellationToken cancellationToken)
    {
        try
        {
            var torrents = await _torrClient.ListTorrentsAsync(cancellationToken);
            var activeHashes = new HashSet<string>(torrents.Select(t => t.Hash), StringComparer.OrdinalIgnoreCase);

            // Sync all active torrents and clean obsolete/duplicate files
            foreach (var torrent in torrents)
            {
                await _strmEngine.SyncTorrentAsync(torrent, cancellationToken: cancellationToken);
            }

            // Clean any orphaned .strm files for deleted torrents
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            foreach (var cat in config.Categories)
            {
                var catFolder = _strmEngine.ResolveMediaRoot(cat.Folder);
                if (!Directory.Exists(catFolder)) continue;

                var strmFiles = Directory.GetFiles(catFolder, "*.strm", SearchOption.AllDirectories);
                foreach (var strmFile in strmFiles)
                {
                    try
                    {
                        var text = await System.IO.File.ReadAllTextAsync(strmFile, cancellationToken);
                        var match = System.Text.RegularExpressions.Regex.Match(text, @"link=([a-f0-9]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (match.Success)
                        {
                            var hash = match.Groups[1].Value;
                            if (!activeHashes.Contains(hash))
                            {
                                System.IO.File.Delete(strmFile);
                                var nfo = Path.ChangeExtension(strmFile, ".nfo");
                                if (System.IO.File.Exists(nfo)) System.IO.File.Delete(nfo);
                                _logger.LogInformation("Purged orphaned .strm file for deleted torrent {Hash}: {File}", hash, strmFile);
                            }
                        }
                    }
                    catch { }
                }

                foreach (var subDir in Directory.GetDirectories(catFolder))
                {
                    _strmEngine.CleanEmptyDirectory(subDir);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during full library sync");
        }

        _strmEngine.TriggerLibraryScan();
        return Ok(new { success = true, message = "Library scan triggered and duplicates cleaned." });
    }

    [HttpPost("Library/ScanTargeted")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult TriggerTargetedScan([FromQuery] string path)
    {
        _strmEngine.NotifyPathChanged(path);
        return Ok(new { success = true, message = $"Targeted scan reported for: {path}" });
    }

    [HttpGet("Items/{itemId}/Alternatives")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MovieAlternativesResponseDto>> GetItemAlternatives(
        [FromRoute] Guid itemId,
        CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item == null)
        {
            return NotFound(new { error = "Item not found in Jellyfin library." });
        }

        var targetDir = _strmEngine.ResolveItemDirectory(itemId);
        var existingStreams = !string.IsNullOrWhiteSpace(targetDir)
            ? _strmEngine.GetStreamsForDirectory(targetDir, item.Path)
            : new List<MovieStreamInfoDto>();

        var imdbId = item.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Imdb);
        var tmdbStr = item.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb);
        int? tmdbId = int.TryParse(tmdbStr, out var tid) ? tid : null;

        var searchResults = new List<TorrentSearchResult>();

        // 1. Try search by IMDb ID if available
        if (!string.IsNullOrWhiteSpace(imdbId))
        {
            searchResults = await _searchService.SearchAsync(imdbId, null, null, isUserAdmin: true, cancellationToken);
        }

        // 2. Fallback to original / clean title + year
        if (searchResults.Count == 0)
        {
            var searchTitle = !string.IsNullOrWhiteSpace(item.OriginalTitle) ? item.OriginalTitle : item.Name;
            var yearStr = item.ProductionYear.HasValue ? item.ProductionYear.Value.ToString() : string.Empty;
            var query = $"{searchTitle} {yearStr}".Trim();
            searchResults = await _searchService.SearchAsync(query, null, null, isUserAdmin: true, cancellationToken);
        }

        // 3. If still empty and item.Name is localized (different from originalTitle), try localized name
        if (searchResults.Count == 0 && !string.IsNullOrWhiteSpace(item.Name) && !item.Name.Equals(item.OriginalTitle, StringComparison.OrdinalIgnoreCase))
        {
            var yearStr = item.ProductionYear.HasValue ? item.ProductionYear.Value.ToString() : string.Empty;
            var query = $"{item.Name} {yearStr}".Trim();
            searchResults = await _searchService.SearchAsync(query, null, null, isUserAdmin: true, cancellationToken);
        }

        var resp = new MovieAlternativesResponseDto
        {
            ItemId = itemId,
            Title = item.Name,
            OriginalTitle = item.OriginalTitle ?? item.Name,
            Year = item.ProductionYear,
            ImdbId = imdbId,
            TmdbId = tmdbId,
            ExistingStreams = existingStreams,
            Alternatives = searchResults
        };

        return Ok(resp);
    }

    [HttpGet("Items/{itemId}/Streams")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<List<MovieStreamInfoDto>> GetItemStreams([FromRoute] Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item == null)
        {
            return NotFound(new { error = "Item not found in Jellyfin library." });
        }

        var targetDir = _strmEngine.ResolveItemDirectory(itemId);
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            return Ok(new List<MovieStreamInfoDto>());
        }

        var streams = _strmEngine.GetStreamsForDirectory(targetDir, item.Path);
        return Ok(streams);
    }

    [HttpPost("Items/{itemId}/AlternateStream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AlternateStreamResultDto>> AddItemAlternateStream(
        [FromRoute] Guid itemId,
        [FromBody] AddAlternateStreamRequestDto req,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(req.Link))
        {
            return BadRequest(new { error = "Torrent link or magnet is required." });
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item == null)
        {
            return NotFound(new { error = "Item not found in Jellyfin library." });
        }

        var targetDir = _strmEngine.ResolveItemDirectory(itemId);
        if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
        {
            return BadRequest(new { error = "Could not resolve target directory for this item." });
        }

        req.TargetItemId = itemId;
        req.TargetFolderPath = targetDir;
        req.LocalizedTitle = item.Name;
        req.OriginalTitle = item.OriginalTitle;
        req.Year = item.ProductionYear;
        var tmdbStr = item.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb);
        req.TmdbId = int.TryParse(tmdbStr, out var tid) ? tid : null;
        req.ImdbId = item.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Imdb);

        // Add torrent to TorrServer
        var torrent = await _torrClient.AddTorrentAsync(
            link: req.Link,
            title: null,
            poster: null,
            category: null,
            saveToDb: true,
            cancellationToken: cancellationToken);

        if (torrent == null)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Failed to communicate with TorrServer." });
        }

        var result = await _strmEngine.AddAlternateStreamAsync(targetDir, torrent, req, cancellationToken);
        if (!result.Success)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = result.ErrorMessage });
        }

        // If replace existing was requested and old hash provided, remove from TorrServer
        if (req.ReplaceExisting && !string.IsNullOrWhiteSpace(req.OldTorrentHash))
        {
            await _torrClient.RemoveTorrentAsync(req.OldTorrentHash, cancellationToken);
        }

        return Ok(result);
    }

    [HttpDelete("Items/{itemId}/Streams/{hash}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteItemStream(
        [FromRoute] Guid itemId,
        [FromRoute] string hash,
        CancellationToken cancellationToken)
    {
        var targetDir = _strmEngine.ResolveItemDirectory(itemId);
        if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
        {
            return NotFound(new { error = "Directory for item not found." });
        }

        var deletedFromDisk = await _strmEngine.DeleteStreamByHashAsync(targetDir, hash, cancellationToken);
        await _torrClient.RemoveTorrentAsync(hash, cancellationToken);

        return Ok(new { success = true, deletedFromDisk });
    }
}
