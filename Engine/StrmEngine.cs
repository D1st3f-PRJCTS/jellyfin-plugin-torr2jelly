using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
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
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly IApplicationPaths _appPaths;
    private readonly TorrServerDaemonManager _daemonManager;
    private readonly HttpClient _httpClient;

    public StrmEngine(
        ILogger<StrmEngine> logger,
        ILibraryManager libraryManager,
        ILibraryMonitor libraryMonitor,
        IApplicationPaths appPaths,
        TorrServerDaemonManager daemonManager,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _appPaths = appPaths;
        _daemonManager = daemonManager;
        _httpClient = httpClientFactory.CreateClient("StrmHttpClient");
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
    }

    public static string FormatTemplate(string template, Dictionary<string, string> tokens)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return string.Empty;
        }

        var result = template;
        foreach (var (key, value) in tokens)
        {
            result = result.Replace("{" + key + "}", value, StringComparison.OrdinalIgnoreCase);
        }

        // Clean up empty parentheses or brackets if a token was empty (e.g. "Title ()" -> "Title")
        result = Regex.Replace(result, @"\(\s*\)", "").Trim();
        result = Regex.Replace(result, @"\[\s*\]", "").Trim();
        result = Regex.Replace(result, @"\s{2,}", " ").Trim();

        return result;
    }

    public string ResolveMediaRoot(string? targetFolder = null)
    {
        // 0. If targetFolder is explicitly an absolute path specified by the user in CategoryMapping
        if (!string.IsNullOrWhiteSpace(targetFolder) && Path.IsPathRooted(targetFolder))
        {
            try
            {
                Directory.CreateDirectory(targetFolder);
            }
            catch { }
            return targetFolder;
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        // 1. Explicit configured root path
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
        bool forceCategory = false,
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

            // Determine initial category
            var chosenCat = config.Categories.FirstOrDefault(c => c.Name.Equals(category, StringComparison.OrdinalIgnoreCase))
                ?? config.Categories.FirstOrDefault(c => c.IsDefault)
                ?? new CategoryMapping("Movies", "movies", "movie", new List<int>());

            var files = torrent.FileStats ?? new List<TorrTorrentFile>();
            var videoFiles = files.Where(f => EpisodeMatcher.IsVideoFile(f.Path)).ToList();

            // If no video files detected yet (e.g. metadata still loading in TorrServer), treat file 0 as video
            if (videoFiles.Count == 0 && files.Count > 0)
            {
                videoFiles.Add(files[0]);
            }
            else if (videoFiles.Count == 0 && files.Count == 0)
            {
                videoFiles.Add(new TorrTorrentFile { Id = 1, Path = $"{cleanTitle}.mkv", Length = torrent.TorrentSize });
            }

            result.VideoFilesCount = videoFiles.Count;

            // Detect if this is a TV series release
            var isDetectedShow = EpisodeMatcher.IsShowRelease(rawTitle, videoFiles.Select(f => f.Path)) || videoFiles.Count > 1;
            var isShow = chosenCat.Type.Equals("show", StringComparison.OrdinalIgnoreCase) || isDetectedShow;

            // Auto-route series to Show category ONLY IF category was not explicitly specified/forced
            if (!forceCategory && string.IsNullOrWhiteSpace(category) && isShow && chosenCat.Type.Equals("movie", StringComparison.OrdinalIgnoreCase))
            {
                var showCat = config.Categories.FirstOrDefault(c => c.Type.Equals("show", StringComparison.OrdinalIgnoreCase))
                    ?? new CategoryMapping("Shows", "shows", "show", new List<int>());

                _logger.LogInformation("Torrent '{Title}' detected as a TV series. Auto-routing from '{OldCategory}' to '{NewCategory}'", rawTitle, chosenCat.Name, showCat.Name);
                chosenCat = showCat;
            }

            result.Category = chosenCat.Name;
            var categoryFolder = ResolveMediaRoot(chosenCat.Folder);
            Directory.CreateDirectory(categoryFolder);

            if (!isShow)
            {
                // Single movie
                var folderTemplate = !string.IsNullOrWhiteSpace(config.MovieFolderTemplate) ? config.MovieFolderTemplate : "{Title} ({Year})";
                var fileTemplate = !string.IsNullOrWhiteSpace(config.MovieFileTemplate) ? config.MovieFileTemplate : "{Title} ({Year}).strm";

                var tokens = new Dictionary<string, string>
                {
                    ["Title"] = cleanTitle,
                    ["Year"] = extractedYear.HasValue ? extractedYear.Value.ToString() : string.Empty,
                    ["RawTitle"] = rawTitle
                };

                string folderName;
                if (config.EnableMovieRenaming)
                {
                    folderName = TitleCleaner.SanitizeFilename(FormatTemplate(folderTemplate, tokens));
                    if (string.IsNullOrWhiteSpace(folderName))
                    {
                        folderName = TitleCleaner.SanitizeFilename(TitleCleaner.FormatFolderTitle(cleanTitle, extractedYear));
                    }
                }
                else
                {
                    folderName = TitleCleaner.SanitizeFilename(rawTitle);
                }

                if (string.IsNullOrWhiteSpace(folderName))
                {
                    folderName = TitleCleaner.SanitizeFilename(rawTitle);
                }

                var movieDir = Path.Combine(categoryFolder, folderName);
                Directory.CreateDirectory(movieDir);
                result.FolderPath = movieDir;

                var mainFile = videoFiles.OrderByDescending(f => f.Length).First();
                string strmName;
                if (config.EnableMovieRenaming)
                {
                    var fileName = FormatTemplate(fileTemplate, tokens);
                    if (!fileName.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
                    {
                        fileName += ".strm";
                    }
                    strmName = TitleCleaner.SanitizeFilename(fileName);
                }
                else
                {
                    var origBase = Path.GetFileNameWithoutExtension(mainFile.Path);
                    if (string.IsNullOrWhiteSpace(origBase))
                    {
                        origBase = rawTitle;
                    }
                    strmName = TitleCleaner.SanitizeFilename(origBase) + ".strm";
                }

                var strmPath = Path.Combine(movieDir, strmName);
                var streamUrl = $"{_daemonManager.GetStreamBaseUrl()}/stream?link={torrent.Hash}&index={mainFile.Id}&play\n";
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

                // Fast targeted scan notification
                NotifyPathChanged(movieDir);
            }
            else
            {
                // Multi-episode series
                var showFolderTemplate = !string.IsNullOrWhiteSpace(config.ShowFolderTemplate) ? config.ShowFolderTemplate : "{Title} ({Year})";
                var seasonFolderTemplate = !string.IsNullOrWhiteSpace(config.ShowSeasonFolderTemplate) ? config.ShowSeasonFolderTemplate : "Season {Season:00}";
                var epFileTemplate = !string.IsNullOrWhiteSpace(config.ShowEpisodeFileTemplate) ? config.ShowEpisodeFileTemplate : "{Title} - {EpisodeTag}.strm";

                var showTokens = new Dictionary<string, string>
                {
                    ["Title"] = cleanTitle,
                    ["Year"] = extractedYear.HasValue ? extractedYear.Value.ToString() : string.Empty,
                    ["RawTitle"] = rawTitle
                };

                string seriesFolder;
                if (config.EnableShowRenaming)
                {
                    seriesFolder = TitleCleaner.SanitizeFilename(FormatTemplate(showFolderTemplate, showTokens));
                    if (string.IsNullOrWhiteSpace(seriesFolder))
                    {
                        seriesFolder = TitleCleaner.SanitizeFilename(TitleCleaner.FormatFolderTitle(cleanTitle, extractedYear));
                    }
                }
                else
                {
                    seriesFolder = TitleCleaner.SanitizeFilename(rawTitle);
                }

                if (string.IsNullOrWhiteSpace(seriesFolder))
                {
                    seriesFolder = TitleCleaner.SanitizeFilename(rawTitle);
                }

                var seriesDir = Path.Combine(categoryFolder, seriesFolder);
                Directory.CreateDirectory(seriesDir);
                result.FolderPath = seriesDir;

                var episodeIndex = 1;
                foreach (var videoFile in videoFiles.OrderBy(f => f.Path))
                {
                    var epInfo = EpisodeMatcher.ParseEpisodeInfo(videoFile.Path, episodeIndex++);
                    var seasonTokens = new Dictionary<string, string>(showTokens)
                    {
                        ["Season"] = epInfo.SeasonNumber.ToString(),
                        ["Season:00"] = epInfo.SeasonNumber.ToString("D2"),
                        ["SeasonFolder"] = epInfo.FormatSeasonFolder()
                    };

                    var seasonFolderName = TitleCleaner.SanitizeFilename(FormatTemplate(seasonFolderTemplate, seasonTokens));
                    if (string.IsNullOrWhiteSpace(seasonFolderName))
                    {
                        seasonFolderName = epInfo.FormatSeasonFolder();
                    }

                    var seasonDir = Path.Combine(seriesDir, seasonFolderName);
                    Directory.CreateDirectory(seasonDir);

                    var epTag = epInfo.FormatEpisodeTag();
                    var epTokens = new Dictionary<string, string>(seasonTokens)
                    {
                        ["Episode"] = epInfo.EpisodeNumber.ToString(),
                        ["Episode:00"] = epInfo.EpisodeNumber.ToString("D2"),
                        ["EpisodeTag"] = epTag
                    };

                    string epFileName;
                    if (config.EnableShowRenaming)
                    {
                        epFileName = FormatTemplate(epFileTemplate, epTokens);
                        if (!epFileName.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
                        {
                            epFileName += ".strm";
                        }
                    }
                    else
                    {
                        var origEpBase = Path.GetFileNameWithoutExtension(videoFile.Path);
                        if (string.IsNullOrWhiteSpace(origEpBase))
                        {
                            origEpBase = $"Episode {episodeIndex:00}";
                        }
                        epFileName = origEpBase + ".strm";
                    }

                    var strmFileName = TitleCleaner.SanitizeFilename(epFileName);
                    var strmPath = Path.Combine(seasonDir, strmFileName);

                    var streamUrl = $"{_daemonManager.GetStreamBaseUrl()}/stream?link={torrent.Hash}&index={videoFile.Id}&play\n";
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

                // Fast targeted scan notification
                NotifyPathChanged(seriesDir);
            }

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
                var parent = Path.GetDirectoryName(folderPath);
                Directory.Delete(folderPath, recursive: true);
                _logger.LogInformation("Deleted torrent folder: {Path}", folderPath);

                if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
                {
                    NotifyPathChanged(parent);
                }
                else
                {
                    TriggerLibraryScan();
                }

                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete directory {Path}", folderPath);
        }

        return false;
    }

    public void NotifyPathChanged(string folderPath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(folderPath))
            {
                _libraryMonitor.ReportFileSystemChangeBeginning(folderPath);
                _libraryMonitor.ReportFileSystemChangeComplete(folderPath, refreshPath: true);
                _logger.LogInformation("Targeted library change reported to Jellyfin LibraryMonitor for path: {Path}", folderPath);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to report targeted filesystem change for {Path}, falling back to full library scan", folderPath);
        }

        TriggerLibraryScan();
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
