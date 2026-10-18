using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Torr2Jelly.Configuration;
using Jellyfin.Plugin.Torr2Jelly.Daemon;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torr2Jelly.Engine;

public class StrmSyncResult
{
    public string TorrentHash { get; set; } = string.Empty;
    public string TorrentTitle { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string FolderPath { get; set; } = string.Empty;
    public List<string> CreatedStrmFiles { get; set; } = new();
    public int VideoFilesCount { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}

public class StrmEngine
{
    private readonly ILogger<StrmEngine> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IApplicationPaths _appPaths;
    private readonly TorrServerDaemonManager _daemonManager;
    private readonly HttpClient _httpClient;

    public StrmEngine(
        ILogger<StrmEngine> logger,
        ILibraryManager libraryManager,
        IApplicationPaths appPaths,
        TorrServerDaemonManager daemonManager,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _appPaths = appPaths;
        _daemonManager = daemonManager;
        _httpClient = httpClientFactory.CreateClient("StrmHttpClient");
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
    }

    public string ResolveMediaRoot(string? targetFolder = null)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        // 1. Explicit configured path
        if (!string.IsNullOrWhiteSpace(config.MediaRootPath) && Directory.Exists(config.MediaRootPath))
        {
            return string.IsNullOrWhiteSpace(targetFolder)
                ? config.MediaRootPath
                : Path.Combine(config.MediaRootPath, targetFolder);
        }

        // 2. Query Jellyfin virtual folders (libraries)
        var virtualFolders = _libraryManager.GetVirtualFolders();
        if (!string.IsNullOrWhiteSpace(targetFolder))
        {
            var match = virtualFolders.FirstOrDefault(vf =>
                vf.Name.Equals(targetFolder, StringComparison.OrdinalIgnoreCase) ||
                vf.Locations.Any(loc => loc.Contains(targetFolder, StringComparison.OrdinalIgnoreCase)));

            if (match != null && match.Locations.Length > 0 && Directory.Exists(match.Locations[0]))
            {
                return match.Locations[0];
            }
        }

        // 3. Fallback to first library location or Jellyfin DataPath/media
        if (virtualFolders.Count > 0 && virtualFolders[0].Locations.Length > 0 && Directory.Exists(virtualFolders[0].Locations[0]))
        {
            var baseDir = Path.GetDirectoryName(virtualFolders[0].Locations[0]) ?? virtualFolders[0].Locations[0];
            return string.IsNullOrWhiteSpace(targetFolder) ? baseDir : Path.Combine(baseDir, targetFolder);
        }

        var defaultMedia = Path.Combine(_appPaths.DataPath, "media");
        Directory.CreateDirectory(defaultMedia);
        return string.IsNullOrWhiteSpace(targetFolder) ? defaultMedia : Path.Combine(defaultMedia, targetFolder);
    }

    public async Task<StrmSyncResult> SyncTorrentAsync(
        TorrTorrent torrent,
        string? category = null,
        string? customTitle = null,
        string? posterUrl = null,
        int? tmdbId = null,
        CancellationToken cancellationToken = default)
    {
        var result = new StrmSyncResult
        {
            TorrentHash = torrent.Hash,
            TorrentTitle = torrent.Title
        };

        try
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var rawTitle = !string.IsNullOrWhiteSpace(customTitle) ? customTitle : torrent.Title;
            var (cleanTitle, extractedYear) = TitleCleaner.CleanTitleAndExtractYear(rawTitle);

            // Determine category
            var chosenCat = config.Categories.FirstOrDefault(c => c.Name.Equals(category, StringComparison.OrdinalIgnoreCase))
                ?? config.Categories.FirstOrDefault(c => c.IsDefault)
                ?? new CategoryMapping("Movies", "movies", "movie", new List<int>());

            result.Category = chosenCat.Name;
            var categoryFolder = ResolveMediaRoot(chosenCat.Folder);
            Directory.CreateDirectory(categoryFolder);

            var files = torrent.FileStats ?? new List<TorrTorrentFile>();
            var videoFiles = files.Where(f => EpisodeMatcher.IsVideoFile(f.Path)).ToList();

            // If no video files detected yet (e.g. metadata still loading in TorrServer), treat file 0 as video
            if (videoFiles.Count == 0 && files.Count > 0)
            {
                videoFiles.Add(files[0]);
            }
            else if (videoFiles.Count == 0 && files.Count == 0)
            {
                videoFiles.Add(new TorrTorrentFile { Id = 0, Path = $"{cleanTitle}.mkv", Length = torrent.TorrentSize });
            }

            result.VideoFilesCount = videoFiles.Count;
            var isShow = chosenCat.Type.Equals("show", StringComparison.OrdinalIgnoreCase) || videoFiles.Count > 1;

            if (!isShow)
            {
                // Single movie
                var folderName = TitleCleaner.SanitizeFilename(TitleCleaner.FormatFolderTitle(cleanTitle, extractedYear));
                var movieDir = Path.Combine(categoryFolder, folderName);
                Directory.CreateDirectory(movieDir);
                result.FolderPath = movieDir;

                var mainFile = videoFiles.OrderByDescending(f => f.Length).First();
                var strmName = $"{folderName}.strm";
                var strmPath = Path.Combine(movieDir, strmName);

                var streamUrl = $"{_daemonManager.GetStreamBaseUrl()}/stream/{Uri.EscapeDataString(folderName + ".mkv")}?link={torrent.Hash}&index={mainFile.Id}&preload";
                await File.WriteAllTextAsync(strmPath, streamUrl, Encoding.UTF8, cancellationToken);
                result.CreatedStrmFiles.Add(strmPath);

                // Write NFO if TMDB ID provided
                if (tmdbId.HasValue && tmdbId.Value > 0)
                {
                    await WriteMovieNfoAsync(movieDir, folderName, cleanTitle, extractedYear, tmdbId.Value, cancellationToken);
                }

                // Download poster if provided
                if (!string.IsNullOrWhiteSpace(posterUrl))
                {
                    await DownloadPosterAsync(movieDir, posterUrl, cancellationToken);
                }
            }
            else
            {
                // Multi-episode series
                var seriesFolder = TitleCleaner.SanitizeFilename(TitleCleaner.FormatFolderTitle(cleanTitle, extractedYear));
                var seriesDir = Path.Combine(categoryFolder, seriesFolder);
                Directory.CreateDirectory(seriesDir);
                result.FolderPath = seriesDir;

                var episodeIndex = 1;
                foreach (var videoFile in videoFiles.OrderBy(f => f.Path))
                {
                    var epInfo = EpisodeMatcher.ParseEpisodeInfo(videoFile.Path, episodeIndex++);
                    var seasonDir = Path.Combine(seriesDir, epInfo.FormatSeasonFolder());
                    Directory.CreateDirectory(seasonDir);

                    var epTag = epInfo.FormatEpisodeTag();
                    var strmFileName = TitleCleaner.SanitizeFilename($"{cleanTitle} - {epTag}.strm");
                    var strmPath = Path.Combine(seasonDir, strmFileName);

                    var streamUrl = $"{_daemonManager.GetStreamBaseUrl()}/stream/{Uri.EscapeDataString(cleanTitle + " " + epTag + ".mkv")}?link={torrent.Hash}&index={videoFile.Id}&preload";
                    await File.WriteAllTextAsync(strmPath, streamUrl, Encoding.UTF8, cancellationToken);
                    result.CreatedStrmFiles.Add(strmPath);
                }

                // Write Series NFO if TMDB ID provided
                if (tmdbId.HasValue && tmdbId.Value > 0)
                {
                    await WriteShowNfoAsync(seriesDir, cleanTitle, extractedYear, tmdbId.Value, cancellationToken);
                }

                // Download poster
                if (!string.IsNullOrWhiteSpace(posterUrl))
                {
                    await DownloadPosterAsync(seriesDir, posterUrl, cancellationToken);
                }
            }

            // Trigger targeted library scan in Jellyfin
            TriggerLibraryScan();

            result.Success = true;
            _logger.LogInformation("Successfully generated {Count} .strm files for '{Title}' in {Dir}", result.CreatedStrmFiles.Count, cleanTitle, result.FolderPath);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync STRM files for torrent {Hash}", torrent.Hash);
            result.Success = false;
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    public bool DeleteTorrentStrm(string folderPath)
    {
        try
        {
            if (Directory.Exists(folderPath))
            {
                Directory.Delete(folderPath, recursive: true);
                _logger.LogInformation("Deleted torrent folder: {Path}", folderPath);
                TriggerLibraryScan();
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete directory {Path}", folderPath);
        }

        return false;
    }

    public void TriggerLibraryScan()
    {
        try
        {
            _libraryManager.QueueLibraryScan();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to queue library scan");
        }
    }

    private async Task DownloadPosterAsync(string targetDir, string posterUrl, CancellationToken cancellationToken)
    {
        try
        {
            var posterPath = Path.Combine(targetDir, "poster.jpg");
            if (File.Exists(posterPath))
            {
                return;
            }

            var bytes = await _httpClient.GetByteArrayAsync(posterUrl, cancellationToken);
            await File.WriteAllBytesAsync(posterPath, bytes, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download poster from {Url}", posterUrl);
        }
    }

    private async Task WriteMovieNfoAsync(string movieDir, string folderName, string title, int? year, int tmdbId, CancellationToken cancellationToken)
    {
        try
        {
            var nfoPath = Path.Combine(movieDir, $"{folderName}.nfo");
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\" ?>");
            sb.AppendLine("<movie>");
            sb.AppendLine($"  <title>{System.Security.SecurityElement.Escape(title)}</title>");
            if (year.HasValue)
            {
                sb.AppendLine($"  <year>{year.Value}</year>");
            }
            sb.AppendLine($"  <uniqueid type=\"tmdb\" default=\"true\">{tmdbId}</uniqueid>");
            sb.AppendLine($"  <tmdbid>{tmdbId}</tmdbid>");
            sb.AppendLine("</movie>");

            await File.WriteAllTextAsync(nfoPath, sb.ToString(), Encoding.UTF8, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write movie NFO for {Title}", title);
        }
    }

    private async Task WriteShowNfoAsync(string showDir, string title, int? year, int tmdbId, CancellationToken cancellationToken)
    {
        try
        {
            var nfoPath = Path.Combine(showDir, "tvshow.nfo");
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\" ?>");
            sb.AppendLine("<tvshow>");
            sb.AppendLine($"  <title>{System.Security.SecurityElement.Escape(title)}</title>");
            if (year.HasValue)
            {
                sb.AppendLine($"  <year>{year.Value}</year>");
            }
            sb.AppendLine($"  <uniqueid type=\"tmdb\" default=\"true\">{tmdbId}</uniqueid>");
            sb.AppendLine($"  <tmdbid>{tmdbId}</tmdbid>");
            sb.AppendLine("</tvshow>");

            await File.WriteAllTextAsync(nfoPath, sb.ToString(), Encoding.UTF8, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write show NFO for {Title}", title);
        }
    }
}
