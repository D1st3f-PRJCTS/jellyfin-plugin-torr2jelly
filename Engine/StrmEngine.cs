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
        string? imdbId = null,
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
            var (cleanTitle, originalTitle, extractedYear) = TitleCleaner.CleanTitleAndExtractYear(rawTitle);

            // Determine initial category
            var chosenCat = config.Categories.FirstOrDefault(c => c.Name.Equals(category, StringComparison.OrdinalIgnoreCase))
                ?? config.Categories.FirstOrDefault(c => c.IsDefault)
                ?? new CategoryMapping("Movies", "movies", "movie", new List<int>());

            var files = torrent.GetEffectiveFiles();
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

            // Check if chosen category is explicitly a TV show or Movie category
            var isChosenCatShow = chosenCat.Type.Equals("show", StringComparison.OrdinalIgnoreCase);
            var isChosenCatMovie = chosenCat.Type.Equals("movie", StringComparison.OrdinalIgnoreCase);

            // Detect if this is a TV series release based on show title keywords or episode tags
            var isDetectedShow = EpisodeMatcher.IsShowRelease(rawTitle, videoFiles.Select(f => f.Path));

            // Auto-route to Show category ONLY IF category was not explicitly specified and torrent is a detected show
            if (!forceCategory && string.IsNullOrWhiteSpace(category) && isDetectedShow && isChosenCatMovie)
            {
                var showCat = config.Categories.FirstOrDefault(c => c.Type.Equals("show", StringComparison.OrdinalIgnoreCase))
                    ?? new CategoryMapping("Shows", "shows", "show", new List<int>());

                _logger.LogInformation("Torrent '{Title}' detected as a TV series. Auto-routing from '{OldCategory}' to '{NewCategory}'", rawTitle, chosenCat.Name, showCat.Name);
                chosenCat = showCat;
                isChosenCatShow = true;
                isChosenCatMovie = false;
            }

            // Treat as TV show ONLY if the category is a show category (or auto-routed to a show category)
            var isShow = isChosenCatShow;

            result.Category = chosenCat.Name;
            var categoryFolder = ResolveMediaRoot(chosenCat.Folder);
            Directory.CreateDirectory(categoryFolder);

            if (!isShow)
            {
                // Movies / Movie Collections
                var folderTemplate = !string.IsNullOrWhiteSpace(config.MovieFolderTemplate) ? config.MovieFolderTemplate : "{Title} ({Year})";
                var fileTemplate = !string.IsNullOrWhiteSpace(config.MovieFileTemplate) ? config.MovieFileTemplate : "{Title} ({Year}).strm";

                // Ensure file and folder names on disk are always in English/Latin
                var fileTitle = TitleCleaner.GetEnglishOrLatinTitle(cleanTitle, originalTitle);

                // Avoid duplicating year in folder name if fileTitle already contains extractedYear
                var yearToken = extractedYear.HasValue && !fileTitle.Contains(extractedYear.Value.ToString(), StringComparison.Ordinal)
                    ? extractedYear.Value.ToString()
                    : string.Empty;

                var tokens = new Dictionary<string, string>
                {
                    ["Title"] = fileTitle,
                    ["OriginalTitle"] = originalTitle ?? fileTitle,
                    ["Year"] = yearToken,
                    ["RawTitle"] = rawTitle
                };

                string folderName;
                if (config.EnableMovieRenaming)
                {
                    folderName = TitleCleaner.SanitizeFilename(FormatTemplate(folderTemplate, tokens));
                    if (string.IsNullOrWhiteSpace(folderName))
                    {
                        folderName = TitleCleaner.SanitizeFilename(TitleCleaner.FormatFolderTitle(fileTitle, extractedYear));
                    }
                }
                else
                {
                    folderName = TitleCleaner.SanitizeFilename(rawTitle);
                }

                if (string.IsNullOrWhiteSpace(folderName))
                {
                    folderName = TitleCleaner.SanitizeFilename(fileTitle);
                }

                var movieDir = Path.Combine(categoryFolder, folderName);
                Directory.CreateDirectory(movieDir);
                result.FolderPath = movieDir;

                if (videoFiles.Count == 1)
                {
                    // Single movie file
                    var mainFile = videoFiles[0];
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
                        var cleanMainPath = (mainFile.Path ?? string.Empty).Replace('\\', '/');
                        var mainFileName = Path.GetFileName(cleanMainPath);
                        var origBase = Path.GetFileNameWithoutExtension(mainFileName);
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

                    // Write Movie NFO (always write clean NFO so Jellyfin matches reliably)
                    await WriteMovieNfoAsync(movieDir, folderName, cleanTitle, originalTitle, extractedYear, tmdbId, imdbId, cancellationToken);
                }
                else
                {
                    // Multi-movie collection or multi-part movie in a movie library
                    foreach (var videoFile in videoFiles.OrderBy(f => f.Path))
                    {
                        string strmName;
                        if (config.EnableMovieRenaming)
                        {
                            var fileBase = Path.GetFileNameWithoutExtension(videoFile.Path) ?? string.Empty;
                            var (itemCleanTitle, itemOrigTitle, itemYear) = TitleCleaner.CleanTitleAndExtractYear(fileBase);
                            if (!string.IsNullOrWhiteSpace(itemCleanTitle) && itemCleanTitle.Length > 2)
                            {
                                var itemFileTitle = TitleCleaner.GetEnglishOrLatinTitle(itemCleanTitle, itemOrigTitle);
                                var itemYearToken = itemYear.HasValue && !itemFileTitle.Contains(itemYear.Value.ToString(), StringComparison.Ordinal)
                                    ? itemYear.Value.ToString()
                                    : string.Empty;

                                var itemTokens = new Dictionary<string, string>
                                {
                                    ["Title"] = itemFileTitle,
                                    ["OriginalTitle"] = itemOrigTitle ?? itemFileTitle,
                                    ["Year"] = itemYearToken,
                                    ["RawTitle"] = fileBase
                                };
                                var itemFileName = FormatTemplate(fileTemplate, itemTokens);
                                if (!itemFileName.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
                                {
                                    itemFileName += ".strm";
                                }
                                strmName = TitleCleaner.SanitizeFilename(itemFileName);
                            }
                            else
                            {
                                strmName = TitleCleaner.SanitizeFilename(fileBase) + ".strm";
                            }
                        }
                        else
                        {
                            var cleanVidPath = (videoFile.Path ?? string.Empty).Replace('\\', '/');
                            var vidFileName = Path.GetFileName(cleanVidPath);
                            var origBase = Path.GetFileNameWithoutExtension(vidFileName);
                            if (string.IsNullOrWhiteSpace(origBase))
                            {
                                origBase = $"Movie_{videoFile.Id}";
                            }
                            strmName = TitleCleaner.SanitizeFilename(origBase) + ".strm";
                        }

                        var strmPath = Path.Combine(movieDir, strmName);
                        var streamUrl = $"{_daemonManager.GetStreamBaseUrl()}/stream?link={torrent.Hash}&index={videoFile.Id}&play\n";
                        await File.WriteAllTextAsync(strmPath, streamUrl, Encoding.UTF8, cancellationToken);
                        result.CreatedStrmFiles.Add(strmPath);

                        // Also write per-movie NFO for collection movies so Jellyfin can identify each individually
                        var itemBaseWithoutExt = Path.GetFileNameWithoutExtension(strmName);
                        var (partTitle, partOrig, partYear) = TitleCleaner.CleanTitleAndExtractYear(itemBaseWithoutExt);
                        await WriteMovieItemNfoAsync(movieDir, itemBaseWithoutExt, partTitle, partOrig, partYear ?? extractedYear, tmdbId, imdbId, cancellationToken);
                    }
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

                // Ensure file and folder names on disk are always in English/Latin
                var showFileTitle = TitleCleaner.GetEnglishOrLatinTitle(cleanTitle, originalTitle);

                // Avoid duplicating year in show folder name if showFileTitle already contains extractedYear
                var showYearToken = extractedYear.HasValue && !showFileTitle.Contains(extractedYear.Value.ToString(), StringComparison.Ordinal)
                    ? extractedYear.Value.ToString()
                    : string.Empty;

                var showTokens = new Dictionary<string, string>
                {
                    ["Title"] = showFileTitle,
                    ["OriginalTitle"] = originalTitle ?? showFileTitle,
                    ["Year"] = showYearToken,
                    ["RawTitle"] = rawTitle
                };

                string seriesFolder;
                if (config.EnableShowRenaming)
                {
                    seriesFolder = TitleCleaner.SanitizeFilename(FormatTemplate(showFolderTemplate, showTokens));
                    if (string.IsNullOrWhiteSpace(seriesFolder))
                    {
                        seriesFolder = TitleCleaner.SanitizeFilename(TitleCleaner.FormatFolderTitle(showFileTitle, extractedYear));
                    }
                }
                else
                {
                    seriesFolder = TitleCleaner.SanitizeFilename(rawTitle);
                }

                if (string.IsNullOrWhiteSpace(seriesFolder))
                {
                    seriesFolder = TitleCleaner.SanitizeFilename(showFileTitle);
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

                // Write Series tvshow.nfo (always write so Jellyfin identifies series properly)
                await WriteShowNfoAsync(seriesDir, cleanTitle, originalTitle, extractedYear, tmdbId, imdbId, cancellationToken);

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

            // Automatically clean up any obsolete/legacy .strm duplicate files and empty directories for this torrent
            await CleanupObsoleteFilesForTorrentAsync(torrent.Hash, result.CreatedStrmFiles, cancellationToken);

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

    public async Task CleanupObsoleteFilesForTorrentAsync(
        string torrentHash,
        List<string> activeStrmFiles,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var activeSet = new HashSet<string>(activeStrmFiles, StringComparer.OrdinalIgnoreCase);
            var affectedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var cat in config.Categories)
            {
                var catFolder = ResolveMediaRoot(cat.Folder);
                if (!Directory.Exists(catFolder)) continue;

                var strmFiles = Directory.GetFiles(catFolder, "*.strm", SearchOption.AllDirectories);
                foreach (var strmFile in strmFiles)
                {
                    if (activeSet.Contains(strmFile)) continue;

                    bool belongsToTorrent = false;
                    try
                    {
                        var text = await File.ReadAllTextAsync(strmFile, cancellationToken);
                        if (text.Contains(torrentHash, StringComparison.OrdinalIgnoreCase))
                        {
                            belongsToTorrent = true;
                        }
                    }
                    catch
                    {
                        // File read error or lock
                    }

                    if (belongsToTorrent)
                    {
                        try
                        {
                            _logger.LogInformation("Deleting obsolete .strm duplicate for torrent {Hash}: {File}", torrentHash, strmFile);
                            File.Delete(strmFile);

                            var nfoFile = Path.ChangeExtension(strmFile, ".nfo");
                            if (File.Exists(nfoFile))
                            {
                                File.Delete(nfoFile);
                            }

                            var parentDir = Path.GetDirectoryName(strmFile);
                            if (!string.IsNullOrWhiteSpace(parentDir))
                            {
                                affectedDirs.Add(parentDir);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to delete obsolete strm file {File}", strmFile);
                        }
                    }
                }
            }

            // Clean up empty directories left behind
            foreach (var dir in affectedDirs)
            {
                CleanEmptyDirectory(dir);
            }

            // Notify Jellyfin
            foreach (var dir in affectedDirs)
            {
                if (Directory.Exists(dir))
                {
                    NotifyPathChanged(dir);
                }
                else
                {
                    var parent = Path.GetDirectoryName(dir);
                    if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
                    {
                        NotifyPathChanged(parent);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while cleaning up obsolete files for torrent {Hash}", torrentHash);
        }
    }

    public void CleanEmptyDirectory(string dirPath)
    {
        try
        {
            if (!Directory.Exists(dirPath)) return;

            // Check if there are any .strm or video files in this directory or subdirectories
            var hasMediaFiles = Directory.EnumerateFiles(dirPath, "*.*", SearchOption.AllDirectories)
                .Any(f => f.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) || EpisodeMatcher.IsVideoFile(f));

            if (!hasMediaFiles)
            {
                _logger.LogInformation("Deleting empty/orphaned directory: {Dir}", dirPath);
                Directory.Delete(dirPath, recursive: true);

                var parent = Path.GetDirectoryName(dirPath);
                if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
                {
                    CleanEmptyDirectory(parent);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not delete empty dir {Dir}", dirPath);
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

    private async Task WriteMovieNfoAsync(
        string movieDir,
        string folderName,
        string title,
        string? originalTitle,
        int? year,
        int? tmdbId,
        string? imdbId,
        CancellationToken cancellationToken)
    {
        try
        {
            var nfoContent = BuildMovieNfoXml(title, originalTitle, year, tmdbId, imdbId);

            var movieNfoPath = Path.Combine(movieDir, "movie.nfo");
            await File.WriteAllTextAsync(movieNfoPath, nfoContent, Encoding.UTF8, cancellationToken);

            var namedNfoPath = Path.Combine(movieDir, $"{folderName}.nfo");
            if (!namedNfoPath.Equals(movieNfoPath, StringComparison.OrdinalIgnoreCase))
            {
                await File.WriteAllTextAsync(namedNfoPath, nfoContent, Encoding.UTF8, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write movie NFO for {Title}", title);
        }
    }

    private async Task WriteMovieItemNfoAsync(
        string movieDir,
        string itemBaseName,
        string title,
        string? originalTitle,
        int? year,
        int? tmdbId,
        string? imdbId,
        CancellationToken cancellationToken)
    {
        try
        {
            var nfoContent = BuildMovieNfoXml(title, originalTitle, year, tmdbId, imdbId);
            var itemNfoPath = Path.Combine(movieDir, $"{itemBaseName}.nfo");
            await File.WriteAllTextAsync(itemNfoPath, nfoContent, Encoding.UTF8, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write movie item NFO for {Title}", title);
        }
    }

    private static string BuildMovieNfoXml(string title, string? originalTitle, int? year, int? tmdbId, string? imdbId)
    {
        var hasId = (tmdbId.HasValue && tmdbId.Value > 0) || !string.IsNullOrWhiteSpace(imdbId);
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\" ?>");
        sb.AppendLine("<movie>");
        // If external IDs exist, omit title/originaltitle so Jellyfin fetches metadata in library's preferred language.
        // If no external ID exists:
        // - If title contains Cyrillic (e.g. Ukrainian), write <title> since it is already localized.
        // - If title is Latin/English, do NOT write <title> (only write <originaltitle>), so Jellyfin matches
        //   against TMDB and populates the localized (Ukrainian) title rather than locking in English.
        if (!hasId)
        {
            var hasCyrillic = title.Any(c => (c >= 0x0400 && c <= 0x04FF) || (c >= 0x0500 && c <= 0x052F));
            if (hasCyrillic)
            {
                sb.AppendLine($"  <title>{System.Security.SecurityElement.Escape(title)}</title>");
            }
            if (!string.IsNullOrWhiteSpace(originalTitle) && !originalTitle.Equals(title, StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine($"  <originaltitle>{System.Security.SecurityElement.Escape(originalTitle)}</originaltitle>");
            }
            else if (!hasCyrillic && !string.IsNullOrWhiteSpace(title))
            {
                sb.AppendLine($"  <originaltitle>{System.Security.SecurityElement.Escape(title)}</originaltitle>");
            }
        }
        if (year.HasValue)
        {
            sb.AppendLine($"  <year>{year.Value}</year>");
        }
        if (tmdbId.HasValue && tmdbId.Value > 0)
        {
            sb.AppendLine($"  <uniqueid type=\"tmdb\" default=\"true\">{tmdbId.Value}</uniqueid>");
            sb.AppendLine($"  <tmdbid>{tmdbId.Value}</tmdbid>");
        }
        if (!string.IsNullOrWhiteSpace(imdbId))
        {
            var cleanImdb = imdbId.Trim();
            if (!cleanImdb.StartsWith("tt", StringComparison.OrdinalIgnoreCase) && long.TryParse(cleanImdb, out var inum))
            {
                cleanImdb = $"tt{inum:D7}";
            }
            var isDefault = !(tmdbId.HasValue && tmdbId.Value > 0);
            sb.AppendLine($"  <uniqueid type=\"imdb\"{(isDefault ? " default=\"true\"" : "")}>{cleanImdb}</uniqueid>");
            sb.AppendLine($"  <imdbid>{cleanImdb}</imdbid>");
            sb.AppendLine($"  <id>{cleanImdb}</id>");
        }
        sb.AppendLine("</movie>");
        return sb.ToString();
    }

    private async Task WriteShowNfoAsync(
        string showDir,
        string title,
        string? originalTitle,
        int? year,
        int? tmdbId,
        string? imdbId,
        CancellationToken cancellationToken)
    {
        try
        {
            var hasId = (tmdbId.HasValue && tmdbId.Value > 0) || !string.IsNullOrWhiteSpace(imdbId);
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\" ?>");
            sb.AppendLine("<tvshow>");
            // If external IDs exist, omit title/originaltitle so Jellyfin fetches metadata in library's preferred language.
            // If no external ID exists:
            // - If title contains Cyrillic (e.g. Ukrainian), write <title> since it is already localized.
            // - If title is Latin/English, do NOT write <title> (only write <originaltitle>), so Jellyfin matches
            //   against TMDB and populates the localized (Ukrainian) title rather than locking in English.
            if (!hasId)
            {
                var hasCyrillic = title.Any(c => (c >= 0x0400 && c <= 0x04FF) || (c >= 0x0500 && c <= 0x052F));
                if (hasCyrillic)
                {
                    sb.AppendLine($"  <title>{System.Security.SecurityElement.Escape(title)}</title>");
                }
                if (!string.IsNullOrWhiteSpace(originalTitle) && !originalTitle.Equals(title, StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"  <originaltitle>{System.Security.SecurityElement.Escape(originalTitle)}</originaltitle>");
                }
                else if (!hasCyrillic && !string.IsNullOrWhiteSpace(title))
                {
                    sb.AppendLine($"  <originaltitle>{System.Security.SecurityElement.Escape(title)}</originaltitle>");
                }
            }
            if (year.HasValue)
            {
                sb.AppendLine($"  <year>{year.Value}</year>");
            }
            if (tmdbId.HasValue && tmdbId.Value > 0)
            {
                sb.AppendLine($"  <uniqueid type=\"tmdb\" default=\"true\">{tmdbId.Value}</uniqueid>");
                sb.AppendLine($"  <tmdbid>{tmdbId.Value}</tmdbid>");
            }
            if (!string.IsNullOrWhiteSpace(imdbId))
            {
                var cleanImdb = imdbId.Trim();
                if (!cleanImdb.StartsWith("tt", StringComparison.OrdinalIgnoreCase) && long.TryParse(cleanImdb, out var inum))
                {
                    cleanImdb = $"tt{inum:D7}";
                }
                var isDefault = !(tmdbId.HasValue && tmdbId.Value > 0);
                sb.AppendLine($"  <uniqueid type=\"imdb\"{(isDefault ? " default=\"true\"" : "")}>{cleanImdb}</uniqueid>");
                sb.AppendLine($"  <imdbid>{cleanImdb}</imdbid>");
                sb.AppendLine($"  <id>{cleanImdb}</id>");
            }
            sb.AppendLine("</tvshow>");

            var nfoPath = Path.Combine(showDir, "tvshow.nfo");
            await File.WriteAllTextAsync(nfoPath, sb.ToString(), Encoding.UTF8, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write show NFO for {Title}", title);
        }
    }
}
