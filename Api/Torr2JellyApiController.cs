using MediaBrowser.Model.Entities;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Torr2Jelly.Api.Dtos;
using Jellyfin.Plugin.Torr2Jelly.Configuration;
using Jellyfin.Plugin.Torr2Jelly.Daemon;
using Jellyfin.Plugin.Torr2Jelly.Engine;
using Jellyfin.Plugin.Torr2Jelly.Search;
using Jellyfin.Plugin.Torr2Jelly.Search.Models;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torr2Jelly.Api;

/// <summary>
/// Administrative API of the plugin. Every action here requires a server administrator; the class-level policy is
/// what protects an action that forgets its own attribute (Jellyfin has no fallback policy).
/// ASP.NET Core combines authorization attributes of a class and its actions (all must pass), so an action cannot
/// relax the class policy: the few actions that any logged-in user may call live in
/// <see cref="Torr2JellyUserApiController"/>.
/// </summary>
[ApiController]
[Route("Plugins/Torr2Jelly")]
[Authorize(Policy = ApiPolicies.RequiresElevation)]
public class Torr2JellyApiController : ControllerBase
{
    private const int MaxTorrentTitleLength = 512;
    private const int MaxPlaceholdersPerRequest = 500;
    private const int MaxEchoedImdbIdLength = 32;
    private const int MaxTmdbKeyLength = 2048;
    private static readonly TimeSpan StreamsTorrServerTimeout = TimeSpan.FromSeconds(3);

    private static readonly SemaphoreSlim SettingsLock = new(1, 1);
    private static string? _searchScript;

    private readonly ILogger<Torr2JellyApiController> _logger;
    private readonly TorrServerDaemonManager _daemonManager;
    private readonly TorrClientService _torrClient;
    private readonly StrmEngine _strmEngine;
    private readonly TorrentSearchService _searchService;
    private readonly ILibraryManager _libraryManager;
    private readonly TorrentLinkResolver _linkResolver;
    private readonly TmdbClient _tmdb;
    private readonly PlaceholderTitleResolver _titleResolver;

    public Torr2JellyApiController(
        ILogger<Torr2JellyApiController> logger,
        TorrServerDaemonManager daemonManager,
        TorrClientService torrClient,
        StrmEngine strmEngine,
        TorrentSearchService searchService,
        ILibraryManager libraryManager,
        TorrentLinkResolver linkResolver,
        TmdbClient tmdb,
        PlaceholderTitleResolver titleResolver)
    {
        _logger = logger;
        _tmdb = tmdb;
        _titleResolver = titleResolver;
        _daemonManager = daemonManager;
        _torrClient = torrClient;
        _strmEngine = strmEngine;
        _searchService = searchService;
        _libraryManager = libraryManager;
        _linkResolver = linkResolver;
    }

    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<PluginStatusDto>> GetStatus(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var torrents = await _torrClient.ListTorrentsAsync(cancellationToken);

        var statusMessage = _daemonManager.StatusMessage;
        if (torrents == null && _daemonManager.IsRunning)
        {
            statusMessage += "; the torrent list could not be read from TorrServer";
        }

        var status = new PluginStatusDto
        {
            IsDaemonRunning = _daemonManager.IsRunning,
            DaemonVersion = _daemonManager.DaemonVersion,
            StatusMessage = statusMessage,
            EffectiveUrl = _daemonManager.GetEffectiveUrl(),
            StreamBaseUrl = _daemonManager.GetStreamBaseUrl(),
            ActiveTorrentsCount = torrents?.Count ?? 0,
            CacheSizeMb = config.CacheSizeMb,
            UseDiskCache = config.UseDiskCache,
            DiskCachePath = config.DiskCachePath
        };

        return Ok(status);
    }

    [HttpGet("Indexers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<IndexerStatusDto>>> GetIndexers(CancellationToken cancellationToken)
    {
        var indexers = await _searchService.GetAvailableIndexersAsync(cancellationToken);
        return Ok(indexers);
    }

    [HttpGet("Torrents")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<List<TorrTorrent>>> GetTorrents(CancellationToken cancellationToken)
    {
        var torrents = await _torrClient.ListTorrentsAsync(cancellationToken);
        if (torrents == null)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "TorrServer is not reachable or returned an error." });
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var uncategorized = torrents.Where(t => string.IsNullOrWhiteSpace(t.Category)).ToList();
        if (uncategorized.Count == 0)
        {
            return Ok(torrents);
        }

        // One index of every .strm on disk instead of a rescan per torrent.
        var index = await Task.Run(() => _strmEngine.BuildStrmIndex(), cancellationToken);
        var toPersist = new List<TorrTorrent>();

        foreach (var t in uncategorized)
        {
            string? located = null;
            if (TorrentHash.IsValid(t.Hash) && index.TryGetValue(TorrentHash.Normalize(t.Hash), out var paths))
            {
                foreach (var path in paths)
                {
                    located = _strmEngine.GetCategoryNameForPath(path);
                    if (located != null)
                    {
                        break;
                    }
                }
            }

            if (located != null)
            {
                // The files already live in this category: remember it in TorrServer.
                t.Category = located;
                toPersist.Add(t);
            }
            else
            {
                // Display only. A guess is never stored: stored categories are enforced by the sync.
                t.Category = GuessCategory(t, config);
            }
        }

        await PersistCategoriesAsync(toPersist);
        return Ok(torrents);
    }

    private static string GuessCategory(TorrTorrent torrent, PluginConfiguration config)
    {
        var isShow = EpisodeMatcher.IsShowRelease(torrent.Title, torrent.FileStats?.Select(f => f.Path) ?? Enumerable.Empty<string>()) ||
                     (torrent.FileStats != null && torrent.FileStats.Count > 1);

        return config.Categories.FirstOrDefault(c => c.Type.Equals(isShow ? "show" : "movie", StringComparison.OrdinalIgnoreCase))?.Name
            ?? config.Categories.FirstOrDefault()?.Name
            ?? (isShow ? "Shows" : "Movies");
    }

    // Awaited with CancellationToken.None: a request that ends early must not abort half of the writes, and
    // the writes must not outlive the request unobserved.
    private async Task PersistCategoriesAsync(List<TorrTorrent> torrents)
    {
        if (torrents.Count == 0)
        {
            return;
        }

        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(torrents.Select(async t =>
        {
            await gate.WaitAsync();
            try
            {
                await _torrClient.SetTorrentCategoryAsync(t.Hash, t.Category, t.Title, t.Poster, CancellationToken.None);
            }
            finally
            {
                gate.Release();
            }
        }));
    }

    [HttpPost("Torrents/{hash}/Recategorize")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<StrmSyncResult>> RecategorizeTorrent(
        [FromRoute] string hash,
        [FromBody] RecategorizeTorrentRequestDto req,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeHash(hash, out var normalizedHash))
        {
            return BadRequest(new { error = "Invalid torrent hash." });
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var category = config.Categories.FirstOrDefault(c =>
            c != null && c.Name.Equals(req.Category?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (category == null)
        {
            return BadRequest(new { error = "Unknown category." });
        }

        var torrent = await _torrClient.GetTorrentAsync(normalizedHash, cancellationToken);
        if (torrent == null)
        {
            return NotFound(new { error = "Torrent not found in TorrServer" });
        }

        // Persist the category first: the sync honours the category stored in TorrServer.
        var saved = await _torrClient.SetTorrentCategoryAsync(normalizedHash, category.Name, torrent.Title, torrent.Poster, cancellationToken);
        if (!saved)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "TorrServer did not accept the category change." });
        }

        torrent.Category = category.Name;

        // The engine's per-torrent cleanup removes the files at the old location once the new ones are written.
        var syncResult = await _strmEngine.SyncTorrentAsync(
            torrent: torrent,
            category: category.Name,
            customTitle: req.CustomTitle,
            forceCategory: true,
            cancellationToken: cancellationToken);

        // The category is saved, but the files were not moved: report it instead of a success. "Metadata not ready"
        // stays a 200: the stored category is honoured by the next sync, which moves the files.
        if (!syncResult.Success &&
            !string.Equals(syncResult.ErrorMessage, StrmEngine.MetadataNotReadyMessage, StringComparison.Ordinal))
        {
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { error = syncResult.ErrorMessage ?? "Failed to write the STRM files; see the Jellyfin log for details." });
        }

        return Ok(syncResult);
    }

    [HttpDelete("Torrents/{hash}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult> DeleteTorrent(
        [FromRoute] string hash,
        [FromQuery] bool deleteStrm = true,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeHash(hash, out var normalizedHash))
        {
            return BadRequest(new { error = "Invalid torrent hash." });
        }

        // The torrent must be gone from TorrServer before any .strm is deleted: otherwise a failed removal would
        // leave a torrent behind that the next sync writes the files for again.
        var removed = await _torrClient.RemoveTorrentAsync(normalizedHash, cancellationToken);
        if (!removed)
        {
            _logger.LogWarning("TorrServer did not remove torrent {Hash}; its files were left in place", normalizedHash);
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Failed to remove the torrent from TorrServer." });
        }

        var deletedFiles = 0;
        if (deleteStrm)
        {
            deletedFiles = await _strmEngine.DeleteTorrentFilesAsync(normalizedHash, cancellationToken);
        }

        return Ok(new { success = true, deletedFiles });
    }

    [HttpGet("Torrents/{hash}/Files")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetTorrentFiles(
        [FromRoute] string hash,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeHash(hash, out var normalizedHash))
        {
            return BadRequest(new { error = "Invalid torrent hash." });
        }

        var torrent = await _torrClient.GetTorrentAsync(normalizedHash, cancellationToken);
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
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> SaveSettings(
        [FromBody] JsonElement body,
        CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "The plugin is not initialised." });
        }

        // Read-merge-write must not interleave with another save.
        await SettingsLock.WaitAsync(cancellationToken);
        try
        {
            var current = plugin.Configuration;
            if (!SettingsMerger.TryMerge(current, body, out var merged, out var error))
            {
                return BadRequest(new { error });
            }

            var serverUrlChanged = !string.Equals(
                current.ExternalTorrServerUrl?.Trim(), merged.ExternalTorrServerUrl, StringComparison.Ordinal);

            plugin.UpdateConfiguration(merged);

            if (serverUrlChanged)
            {
                // Refresh the status shown to the user right away instead of at the next watchdog cycle.
                await _daemonManager.CheckExternalServerAsync(cancellationToken);
            }

            if (merged.ManageTorrServerSettings)
            {
                await _daemonManager.ConfigureDaemonSettingsAsync(cancellationToken);
            }

            return Ok(new { success = true });
        }
        finally
        {
            SettingsLock.Release();
        }
    }

    [HttpPost("Daemon/Restart")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> RestartDaemon(CancellationToken cancellationToken)
    {
        await _daemonManager.RestartAsync(cancellationToken);
        return Ok(new { success = _daemonManager.IsRunning, status = _daemonManager.StatusMessage });
    }

    [HttpGet("search.js")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public ActionResult GetSearchScript()
    {
        var content = LoadSearchScript();
        if (content == null)
        {
            return NotFound("/* Search script resource not found */");
        }

        // The injected script tag carries the plugin version in its query string, so a new release is fetched anew.
        Response.Headers.CacheControl = "public, max-age=3600";
        return Content(content, "application/javascript");
    }

    private static string? LoadSearchScript()
    {
        var cached = _searchScript;
        if (cached != null)
        {
            return cached;
        }

        var assembly = typeof(Plugin).Assembly;
        var resourceName = $"{typeof(Plugin).Namespace}.Web.torr2jellysearch.js";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        _searchScript = reader.ReadToEnd();
        return _searchScript;
    }

    [HttpPost("Library/Scan")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult> TriggerScan(CancellationToken cancellationToken)
    {
        // Only a list TorrServer really returned may drive the purge: a failed request must never look like
        // "there are no torrents".
        var torrents = await _torrClient.ListTorrentsAsync(cancellationToken);
        if (torrents == null)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                error = "TorrServer is not reachable or returned an error; nothing was synced or removed."
            });
        }

        FullSyncReport report;
        try
        {
            report = await _strmEngine.FullSyncAsync(torrents, purgeOrphans: true, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during full library sync");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "The library sync failed; see the Jellyfin log for details." });
        }

        _strmEngine.TriggerLibraryScan();

        var message = torrents.Count == 0
            ? "TorrServer has no torrents; nothing was synced and no files were removed. Library scan triggered."
            : "Library synced and scan triggered.";

        return Ok(new { success = true, message, report });
    }

    [HttpPost("Library/ScanTargeted")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult TriggerTargetedScan([FromQuery] string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !_strmEngine.IsPathInsideManagedRoots(path))
        {
            return BadRequest(new { error = "The path is not inside a configured category folder." });
        }

        _strmEngine.NotifyPathChanged(path);
        return Ok(new { success = true, message = $"Targeted scan reported for: {path}" });
    }

    /// <summary>
    /// Creates placeholders for movies and series: a folder with a placeholder .strm and an NFO (IMDb / TMDb / TVDB
    /// ids) per item, so the title shows up in the library with its poster and metadata before any torrent exists for
    /// it. Nothing is overwritten or deleted, an existing folder or placeholder is reported as "exists", and one
    /// failing item does not stop the others. When a release is added later (Torrent versions - Find releases for a
    /// movie, "Add torrents to this series" for a series), it replaces the placeholder.
    /// </summary>
    [HttpPost("Placeholders")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<CreatePlaceholdersResponseDto>> CreatePlaceholders(
        [FromBody] CreatePlaceholdersRequestDto req,
        CancellationToken cancellationToken)
    {
        if (req?.Items == null || req.Items.Count == 0)
        {
            return ApiError(StatusCodes.Status400BadRequest, "items_required", "Items is required.");
        }

        if (req.Items.Count > MaxPlaceholdersPerRequest)
        {
            return ApiError(StatusCodes.Status400BadRequest, "too_many_items", $"At most {MaxPlaceholdersPerRequest} items per request.");
        }

        var rows = new PlaceholderResultDto[req.Items.Count];
        var movieRequests = new List<PlaceholderRequest>();
        var movieRows = new List<int>();
        var seriesRequests = new List<SeriesPlaceholderRequest>();
        var seriesRows = new List<int>();

        for (var i = 0; i < req.Items.Count; i++)
        {
            var item = req.Items[i];
            var imdb = (item?.ImdbId ?? string.Empty).Trim();
            var tmdb = item?.TmdbId is > 0 ? item.TmdbId : null;
            var tvdb = item?.TvdbId is > 0 ? item.TvdbId : null;

            var kind = (item?.Kind ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "" or "movie" => "movie",
                "series" or "tv" or "show" => "series",
                _ => null
            };

            var row = new PlaceholderResultDto
            {
                ImdbId = imdb.Length <= MaxEchoedImdbIdLength ? imdb : imdb[..MaxEchoedImdbIdLength],
                Kind = kind ?? "movie",
                Status = PlaceholderResult.StatusError
            };
            rows[i] = row;

            if (kind == null)
            {
                row.Reason = "invalid_kind";
                continue;
            }

            // The id the item is known by; a movie has no use for a TVDB id.
            row.Key = imdb.Length > 0 ? row.ImdbId
                : tmdb.HasValue ? "tmdb:" + tmdb.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : kind == "series" && tvdb.HasValue ? "tvdb:" + tvdb.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : string.Empty;
            if (row.Key.Length == 0)
            {
                row.Reason = PlaceholderResult.ReasonNoId;
                continue;
            }

            if (kind == "series")
            {
                seriesRequests.Add(new SeriesPlaceholderRequest(item?.Category ?? string.Empty, item?.Title ?? string.Empty, item?.Year, imdb, tvdb, tmdb));
                seriesRows.Add(i);
            }
            else
            {
                movieRequests.Add(new PlaceholderRequest(item?.Category ?? string.Empty, item?.Title ?? string.Empty, item?.Year, imdb, tmdb));
                movieRows.Add(i);
            }
        }

        try
        {
            if (movieRequests.Count > 0)
            {
                var results = await _strmEngine.CreateMoviePlaceholdersAsync(movieRequests, cancellationToken);
                for (var j = 0; j < results.Count; j++)
                {
                    FillPlaceholderRow(rows[movieRows[j]], results[j]);
                }
            }

            if (seriesRequests.Count > 0)
            {
                var results = await _strmEngine.CreateSeriesPlaceholdersAsync(seriesRequests, cancellationToken);
                for (var j = 0; j < results.Count; j++)
                {
                    FillPlaceholderRow(rows[seriesRows[j]], results[j]);
                }
            }

            return Ok(new CreatePlaceholdersResponseDto { Results = rows.ToList() });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating placeholders");
            return ApiError(StatusCodes.Status500InternalServerError, "placeholders_failed", "Failed to create the placeholders; see the Jellyfin log for details.");
        }
    }

    private static void FillPlaceholderRow(PlaceholderResultDto row, PlaceholderResult result)
    {
        row.Status = result.Status;
        row.Folder = result.Folder;
        row.Reason = result.Reason;
    }

    /// <summary>
    /// Creates placeholders from free-text title lines ("Dune (2021)"): each line is looked up on TMDb and resolved
    /// only when unambiguous (otherwise it is reported as "ambiguous" with up to three candidates).
    /// </summary>
    [HttpPost("Placeholders/FromTitles")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<FromTitlesResponseDto>> CreatePlaceholdersFromTitles(
        [FromBody] FromTitlesRequestDto req,
        CancellationToken cancellationToken)
    {
        var lines = (req?.Lines ?? new List<string>()).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (lines.Count == 0)
        {
            return ApiError(StatusCodes.Status400BadRequest, "lines_required", "Lines is required.");
        }

        if (lines.Count > PlaceholderTitleResolver.MaxLines)
        {
            return ApiError(StatusCodes.Status400BadRequest, "too_many_lines", $"At most {PlaceholderTitleResolver.MaxLines} lines per request.");
        }

        try
        {
            var response = await _titleResolver.ResolveAsync(lines, req?.Kind, req?.Category, cancellationToken);
            return Ok(response);
        }
        catch (TmdbException ex)
        {
            return TmdbError(ex);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating placeholders from titles");
            return ApiError(StatusCodes.Status500InternalServerError, "placeholders_failed", "Failed to create the placeholders; see the Jellyfin log for details.");
        }
    }

    /// <summary>Looks a title up on TMDb (movies and series), marking what is already in the library.</summary>
    [HttpGet("Tmdb/Search")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<List<TmdbResultDto>>> TmdbSearch(
        [FromQuery] string? query = null,
        [FromQuery] string? type = null,
        [FromQuery] int? year = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Ok(new List<TmdbResultDto>());
        }

        if (year is < 1870 or > 2200)
        {
            year = null;
        }

        try
        {
            var matches = await _tmdb.SearchAsync(query, (type ?? string.Empty).Trim().ToLowerInvariant(), year, null, cancellationToken);

            // Copies: the matches are cached and shared between requests.
            var results = matches.Select(m => m.Dto.Clone()).ToList();
            if (results.Count > 0)
            {
                var snapshot = await Task.Run(() => _strmEngine.BuildLibraryIdSnapshot(), cancellationToken);
                PlaceholderTitleResolver.ApplyLibraryState(results, snapshot);
            }

            return Ok(results);
        }
        catch (TmdbException ex)
        {
            return TmdbError(ex);
        }
    }

    /// <summary>Checks a TMDb key or token (the one in the body, else the stored one) with a single search.</summary>
    [HttpPost("Tmdb/Test")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<TmdbTestResultDto>> TmdbTest(
        [FromBody] TmdbTestRequestDto? req,
        CancellationToken cancellationToken)
    {
        var apiKey = req?.ApiKey;
        if (apiKey != null && apiKey.Length > MaxTmdbKeyLength)
        {
            return Ok(new TmdbTestResultDto { Ok = false, Code = TmdbClient.CodeKeyInvalid });
        }

        var code = await _tmdb.TestAsync(apiKey, cancellationToken);
        return Ok(new TmdbTestResultDto { Ok = code == null, Code = code });
    }

    private ObjectResult TmdbError(TmdbException ex)
    {
        return ex.Code switch
        {
            TmdbClient.CodeKeyMissing => ApiError(StatusCodes.Status503ServiceUnavailable, ex.Code, "No TMDb API key is configured."),
            TmdbClient.CodeKeyInvalid => ApiError(StatusCodes.Status502BadGateway, ex.Code, "TMDb rejected the API key."),
            _ => ApiError(StatusCodes.Status502BadGateway, TmdbClient.CodeFailed, "TMDb could not be reached.")
        };
    }

    private ObjectResult ApiError(int status, string code, string message)
    {
        return StatusCode(status, new { error = message, code });
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

        var isAdmin = User.IsInRole(ApiPolicies.AdministratorRole);
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
            searchResults = await _searchService.SearchAsync(imdbId, null, null, isUserAdmin: isAdmin, cancellationToken);
        }

        // 2. Fallback to original / clean title + year
        if (searchResults.Count == 0)
        {
            var searchTitle = !string.IsNullOrWhiteSpace(item.OriginalTitle) ? item.OriginalTitle : item.Name;
            var yearStr = item.ProductionYear.HasValue ? item.ProductionYear.Value.ToString() : string.Empty;
            var query = $"{searchTitle} {yearStr}".Trim();
            searchResults = await _searchService.SearchAsync(query, null, null, isUserAdmin: isAdmin, cancellationToken);
        }

        // 3. If still empty and item.Name is localized (different from originalTitle), try localized name
        if (searchResults.Count == 0 && !string.IsNullOrWhiteSpace(item.Name) && !item.Name.Equals(item.OriginalTitle, StringComparison.OrdinalIgnoreCase))
        {
            var yearStr = item.ProductionYear.HasValue ? item.ProductionYear.Value.ToString() : string.Empty;
            var query = $"{item.Name} {yearStr}".Trim();
            searchResults = await _searchService.SearchAsync(query, null, null, isUserAdmin: isAdmin, cancellationToken);
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
    public async Task<ActionResult<List<MovieStreamInfoDto>>> GetItemStreams([FromRoute] Guid itemId, CancellationToken cancellationToken)
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

        // One TorrServer list call for all versions. TorrServer being slow or down must never fail or delay the page:
        // the rows then simply carry no size / seeders.
        if (streams.Any(s => !string.IsNullOrEmpty(s.TorrentHash)))
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(StreamsTorrServerTimeout);
                StreamInfoEnricher.Apply(streams, await _torrClient.ListTorrentsAsync(cts.Token));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not enrich the versions of item {ItemId} from TorrServer", itemId);
            }
        }

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
        var resolution = await _linkResolver.ResolveAsync(req.Link, cancellationToken);
        if (!resolution.Success)
        {
            return BadRequest(new { error = resolution.Error });
        }

        string? oldHash = null;
        if (!string.IsNullOrWhiteSpace(req.OldTorrentHash))
        {
            if (!TryNormalizeHash(req.OldTorrentHash, out var parsedOldHash))
            {
                return BadRequest(new { error = "OldTorrentHash is not a valid torrent hash." });
            }

            oldHash = parsedOldHash;
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item == null)
        {
            return NotFound(new { error = "Item not found in Jellyfin library." });
        }

        if (item is not Movie)
        {
            return BadRequest(new { error = "Alternate versions can only be added to movies." });
        }

        var targetDir = _strmEngine.ResolveItemDirectory(itemId);
        if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
        {
            return BadRequest(new { error = "Could not resolve target directory for this item." });
        }

        // "Replace" only means something when the old version really is one of this item's streams. Otherwise
        // nothing is replaced and, above all, no unrelated torrent is removed from TorrServer.
        var replaceExisting = false;
        if (req.ReplaceExisting && oldHash != null)
        {
            var knownStreams = _strmEngine.GetStreamsForDirectory(targetDir, item.Path);
            replaceExisting = knownStreams.Any(s => TorrentHash.Equal(s.TorrentHash, oldHash));
            if (!replaceExisting)
            {
                _logger.LogWarning(
                    "Ignoring ReplaceExisting for item {ItemId}: torrent {Hash} has no stream in {Dir}",
                    itemId, oldHash, targetDir);
            }
        }

        req.ReplaceExisting = replaceExisting;
        req.OldTorrentHash = replaceExisting ? oldHash : null;

        req.TargetItemId = itemId;
        req.TargetFolderPath = targetDir;
        req.LocalizedTitle = item.Name;
        req.OriginalTitle = item.OriginalTitle;
        req.Year = item.ProductionYear;
        var tmdbStr = item.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb);
        req.TmdbId = int.TryParse(tmdbStr, out var tid) ? tid : null;
        req.ImdbId = item.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Imdb);

        // Whether TorrServer already holds the torrent decides what a failure may clean up. Null (the list could not
        // be read) counts as "already there": it is better to leave a torrent behind than to remove someone else's.
        var before = await _torrClient.ListTorrentsAsync(cancellationToken);

        // Add torrent to TorrServer
        var torrent = await _torrClient.AddTorrentAsync(
            link: resolution.Link,
            title: null,
            poster: null,
            category: null,
            saveToDb: true,
            cancellationToken: cancellationToken);

        if (torrent == null)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "TorrServer did not accept the torrent." });
        }

        var preExisting = before == null || before.Any(t => TorrentHash.Equal(t.Hash, torrent.Hash));

        var result = await _strmEngine.AddAlternateStreamAsync(targetDir, torrent, req, cancellationToken);
        if (!result.Success)
        {
            // Nothing references the torrent we just added: do not leave it behind in TorrServer.
            var addedHash = TorrentHash.Normalize(torrent.Hash ?? string.Empty);
            if (!preExisting && TorrentHash.IsValid(addedHash))
            {
                await RemoveTorrentIfUnreferencedAsync(addedHash, cancellationToken);
            }

            return StatusCode(StatusCodes.Status500InternalServerError, new { error = result.ErrorMessage });
        }

        // The new version is on disk; only now may the replaced one go from TorrServer. The engine deletes the old
        // .strm only inside the managed category folders: while it is still there, the torrent is still in use.
        if (replaceExisting && oldHash != null && !TorrentHash.Equal(oldHash, torrent.Hash))
        {
            var oldStreamRemains = _strmEngine.GetStreamsForDirectory(targetDir, item.Path)
                .Any(s => TorrentHash.Equal(s.TorrentHash, oldHash));
            if (oldStreamRemains)
            {
                _logger.LogInformation(
                    "Keeping the replaced torrent {Hash} in TorrServer: its .strm file in {Dir} was not removed (outside the managed category folders)",
                    oldHash, targetDir);
            }
            else
            {
                await RemoveTorrentIfUnreferencedAsync(oldHash, cancellationToken);
            }
        }

        return Ok(result);
    }

    /// <summary>
    /// Adds a torrent (typically a new season pack) to an EXISTING series: its episodes are written as .strm files
    /// into that series' folder, laid out by season, so they merge with the seasons already there.
    /// </summary>
    [HttpPost("Items/{itemId}/SeriesTorrent")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<SeriesTorrentResultDto>> AddItemSeriesTorrent(
        [FromRoute] Guid itemId,
        [FromBody] AddSeriesTorrentRequestDto req,
        CancellationToken cancellationToken)
    {
        // Pure validation first: nothing here touches the network or TorrServer.
        if (req == null)
        {
            return BadRequest(new { error = "Torrent link or magnet is required." });
        }

        if (!TorrentLinkResolver.TryNormalize(req.Link, out _, out _, out var linkError))
        {
            return BadRequest(new { error = linkError });
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item == null)
        {
            return NotFound(new { error = "Item not found in Jellyfin library." });
        }

        if (item is not MediaBrowser.Controller.Entities.TV.Series)
        {
            return BadRequest(new { error = "Torrents can only be added to a series here." });
        }

        var seriesDir = _strmEngine.ResolveItemDirectory(itemId);
        if (string.IsNullOrWhiteSpace(seriesDir) || !Directory.Exists(seriesDir))
        {
            return BadRequest(new { error = "Could not resolve the folder of this series." });
        }

        // Refused before TorrServer is asked: the folder must lie inside a category folder or a Jellyfin library (the
        // managed roots, as for IsPathInsideManagedRoots) and must not be such a root itself. The engine checks again
        // under its lock.
        if (!_strmEngine.IsValidSeriesDirectory(seriesDir, out var seriesDirError))
        {
            return BadRequest(new { error = seriesDirError ?? "The folder of this series cannot hold a series torrent." });
        }

        var resolution = await _linkResolver.ResolveAsync(req.Link, cancellationToken);
        if (!resolution.Success)
        {
            return BadRequest(new { error = resolution.Error });
        }

        // Whether TorrServer already holds the torrent decides what a failure may clean up. Null (the list could not
        // be read) counts as "already there": it is better to leave a torrent behind than to remove someone else's.
        var before = await _torrClient.ListTorrentsAsync(cancellationToken);

        var torrentTitle = string.IsNullOrWhiteSpace(req.TorrentTitle) ? null : req.TorrentTitle.Trim();
        if (torrentTitle != null && torrentTitle.Length > MaxTorrentTitleLength)
        {
            torrentTitle = torrentTitle[..MaxTorrentTitleLength];
        }

        // The category of the folder the series lives in (empty when it only lives in a Jellyfin library): if the
        // folder is ever gone, a sync then files the torrent where that series' siblings are.
        var category = _strmEngine.GetCategoryNameForPath(seriesDir) ?? string.Empty;

        var torrent = await _torrClient.AddTorrentAsync(
            link: resolution.Link,
            title: torrentTitle,
            poster: null,
            category: category,
            saveToDb: true,
            cancellationToken: cancellationToken);

        if (torrent == null)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "TorrServer did not accept the torrent." });
        }

        var preExisting = before == null || before.Any(t => TorrentHash.Equal(t.Hash, torrent.Hash));

        var result = await _strmEngine.AddTorrentToSeriesAsync(seriesDir, torrent, cancellationToken);
        if (!result.Success)
        {
            // Nothing references the torrent we just added: do not leave it behind in TorrServer.
            var addedHash = TorrentHash.Normalize(torrent.Hash ?? string.Empty);
            if (!preExisting && TorrentHash.IsValid(addedHash))
            {
                await RemoveTorrentIfUnreferencedAsync(addedHash, cancellationToken);
            }

            // The engine's messages are fixed texts; exception details only ever go to the log.
            ActionResult failure = result.Failure switch
            {
                SeriesTorrentFailure.Conflict => Conflict(new { error = result.Message }),
                SeriesTorrentFailure.InvalidTarget or SeriesTorrentFailure.NoVideoFiles => BadRequest(new { error = result.Message }),
                SeriesTorrentFailure.InvalidHash => StatusCode(StatusCodes.Status502BadGateway, new { error = "TorrServer did not accept the torrent." }),
                _ => StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to add the torrent to the series; see the Jellyfin log for details." })
            };
            return failure;
        }

        return Ok(result);
    }

    [HttpDelete("Items/{itemId}/Streams/{hash}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteItemStream(
        [FromRoute] Guid itemId,
        [FromRoute] string hash,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeHash(hash, out var normalizedHash))
        {
            return BadRequest(new { error = "Invalid torrent hash." });
        }

        var targetDir = _strmEngine.ResolveItemDirectory(itemId);
        if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
        {
            return NotFound(new { error = "Directory for item not found." });
        }

        // The engine only deletes inside the configured category folders; say so instead of a misleading "no such stream".
        if (!_strmEngine.IsPathInsideManagedRoots(targetDir))
        {
            return BadRequest(new { error = "This item is outside the configured category folders, so its streams cannot be removed here." });
        }

        var deletedFromDisk = await _strmEngine.DeleteStreamByHashAsync(targetDir, normalizedHash, cancellationToken);
        if (!deletedFromDisk)
        {
            return NotFound(new { error = "This item has no stream with that hash." });
        }

        var removedFromTorrServer = await RemoveTorrentIfUnreferencedAsync(normalizedHash, cancellationToken);

        return Ok(new { success = true, deletedFromDisk, removedFromTorrServer });
    }

    // Removes a torrent from TorrServer once no .strm file of the library points at it any more.
    private async Task<bool> RemoveTorrentIfUnreferencedAsync(string hash, CancellationToken cancellationToken)
    {
        var index = await Task.Run(() => _strmEngine.BuildStrmIndex(), cancellationToken);
        if (index.TryGetValue(hash, out var remaining) && remaining.Any(System.IO.File.Exists))
        {
            _logger.LogInformation("Keeping torrent {Hash} in TorrServer: other .strm files still use it", hash);
            return false;
        }

        var removed = await _torrClient.RemoveTorrentAsync(hash, cancellationToken);
        if (!removed)
        {
            _logger.LogWarning("TorrServer did not remove torrent {Hash}", hash);
        }

        return removed;
    }

    private static bool TryNormalizeHash(string? hash, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(hash))
        {
            return false;
        }

        var candidate = TorrentHash.Normalize(hash);
        if (!TorrentHash.IsValid(candidate))
        {
            return false;
        }

        normalized = candidate;
        return true;
    }
}
