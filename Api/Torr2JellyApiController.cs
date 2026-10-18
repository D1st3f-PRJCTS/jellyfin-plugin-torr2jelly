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
            DaemonPid = _daemonManager.ProcessId,
            DaemonVersion = _daemonManager.DaemonVersion,
            DaemonPort = config.EmbeddedTorrServerPort,
            Mode = config.UseEmbeddedTorrServer ? "Embedded" : "External",
            StatusMessage = _daemonManager.StatusMessage,
            EffectiveUrl = _daemonManager.GetEffectiveUrl(),
            StreamBaseUrl = _daemonManager.GetStreamBaseUrl(),
            ActiveTorrentsCount = torrents.Count
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
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Ok(new List<TorrentSearchResult>());
        }

        var results = await _searchService.SearchAsync(query, category, onlyGold, isUserAdmin: true, cancellationToken);
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

        var oldConfig = Plugin.Instance.Configuration;
        var needsDaemonRestart = oldConfig.UseEmbeddedTorrServer != newConfig.UseEmbeddedTorrServer ||
                                 oldConfig.EmbeddedTorrServerPort != newConfig.EmbeddedTorrServerPort;

        Plugin.Instance.UpdateConfiguration(newConfig);

        if (needsDaemonRestart)
        {
            await _daemonManager.RestartAsync(cancellationToken);
        }
        else
        {
            await _daemonManager.ConfigureDaemonSettingsAsync(cancellationToken);
        }

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
    public ActionResult TriggerScan()
    {
        _strmEngine.TriggerLibraryScan();
        return Ok(new { success = true, message = "Library scan triggered." });
    }

    [HttpPost("Library/ScanTargeted")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult TriggerTargetedScan([FromQuery] string path)
    {
        _strmEngine.NotifyPathChanged(path);
        return Ok(new { success = true, message = $"Targeted scan reported for: {path}" });
    }
}
