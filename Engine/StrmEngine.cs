using Jellyfin.Plugin.Torr2Jelly.Api.Dtos;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
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

/// <summary>Outcome of <see cref="StrmEngine.FullSyncAsync"/>.</summary>
public class FullSyncReport
{
    /// <summary>Torrents whose .strm files are in place (written, unchanged, or an alternate version left alone).</summary>
    public int Synced { get; set; }

    /// <summary>Torrents that could not be synced (invalid hash, disk error, category not permitted).</summary>
    public int Failed { get; set; }

    /// <summary>Torrents skipped because TorrServer has not loaded their file list yet and they already have .strm files.</summary>
    public int SkippedNotReady { get; set; }

    /// <summary>.strm files (with sidecar .nfo) of torrents that no longer exist in TorrServer.</summary>
    public int OrphansDeleted { get; set; }

    public List<string> Errors { get; set; } = new();
}

/// <summary>Outcome of <see cref="StrmEngine.CreateMoviePlaceholderAsync"/> for one movie.</summary>
public class PlaceholderResult
{
    /// <summary>The placeholder files were written.</summary>
    public const string StatusCreated = "created";

    /// <summary>The movie already has a folder with a stream (or a placeholder): nothing was written.</summary>
    public const string StatusExists = "exists";

    /// <summary>The request was refused or could not be carried out; <see cref="Reason"/> is a fixed code.</summary>
    public const string StatusError = "error";

    // Fixed reason codes: no exception text, no paths.
    public const string ReasonInvalidImdb = "invalid_imdb";
    public const string ReasonInvalidTitle = "invalid_title";
    public const string ReasonInvalidYear = "invalid_year";
    public const string ReasonUnknownCategory = "unknown_category";
    public const string ReasonNotMovieCategory = "not_a_movie_category";
    public const string ReasonCategoryUnavailable = "category_unavailable";
    public const string ReasonFolderHasStream = "folder_has_stream";
    public const string ReasonPlaceholderExists = "placeholder_exists";
    public const string ReasonWriteFailed = "write_failed";

    /// <summary><see cref="StatusCreated"/>, <see cref="StatusExists"/> or <see cref="StatusError"/>.</summary>
    public string Status { get; set; } = StatusError;

    /// <summary>The movie folder (created, or the one that already holds the stream / placeholder).</summary>
    public string? Folder { get; set; }

    /// <summary>A fixed reason code for <see cref="StatusExists"/> and <see cref="StatusError"/>; null when created.</summary>
    public string? Reason { get; set; }
}

/// <summary>One movie to create a placeholder for (see <see cref="StrmEngine.CreateMoviePlaceholdersAsync"/>).</summary>
public sealed record PlaceholderRequest(string Category, string Title, int? Year, string ImdbId, int? TmdbId);

public class StrmEngine
{
    /// <summary><see cref="StrmSyncResult.ErrorMessage"/> used when a torrent's file list is not available yet.</summary>
    public const string MetadataNotReadyMessage = "metadata not ready";

    /// <summary>
    /// Marks the .strm of a movie placeholder: its single line is <c>{streamBase}/t2j-placeholder?imdb={ttid}</c>.
    /// Such a file carries no <c>link=</c> hash, so every hash-based routine ignores it.
    /// </summary>
    public const string PlaceholderMarker = "t2j-placeholder";

    private const int MaxPlaceholderTitleLength = 512;
    private const int MinPlaceholderYear = 1870;
    private const int MaxPlaceholderYear = 2200;

    // The imdb id inside a placeholder URL ("...t2j-placeholder?imdb=tt0096874").
    private static readonly Regex PlaceholderImdbRegex =
        new(Regex.Escape(PlaceholderMarker) + @"\?imdb=(tt[0-9]{1,10})(?=[&#\s]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PlaceholderRequestImdbRegex = new(@"^tt[0-9]{1,10}$", RegexOptions.Compiled);

    private const int MaxReportedErrors = 100;
    private const int MaxPruneLevels = 3;
    private const int MaxDisposableDepth = 8;
    private const int MaxStrmReadChars = 4096;
    private const long MaxPosterBytes = 10L * 1024 * 1024;

    // A .strm younger than this is never purged as an orphan: it may belong to a torrent that was added after the
    // torrent list used by the purge was fetched.
    private static readonly TimeSpan OrphanGracePeriod = TimeSpan.FromMinutes(2);

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly EnumerationOptions StrmTreeEnumeration = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        MatchCasing = MatchCasing.CaseInsensitive
    };

    private static readonly EnumerationOptions StrmTopLevelEnumeration = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        MatchCasing = MatchCasing.CaseInsensitive
    };

    // Files that may be removed together with their directory once the last .strm is gone.
    private static readonly HashSet<string> DisposableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".nfo", ".jpg", ".jpeg", ".png", ".webp", ".tbn", ".bif"
    };

    // NFO files that describe a whole movie folder / show / season and are never the sidecar of one .strm.
    private static readonly HashSet<string> SharedNfoNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "movie.nfo", "tvshow.nfo", "season.nfo"
    };

    // Name given to the roots built from Jellyfin library locations (see GetLibraryRoots).
    private const string LibraryRootName = "(library)";

    /// <summary>
    /// A canonical directory the engine may work inside. Normally a category folder; roots built from Jellyfin
    /// library locations reuse the record with <see cref="LibraryRootName"/> and are only ever combined with the
    /// category roots for explicit, hash-targeted operations (see GetManagedRoots).
    /// </summary>
    private sealed record CategoryRoot(string Name, string Dir);

    private readonly ILogger<StrmEngine> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly IApplicationPaths _appPaths;
    private readonly TorrServerDaemonManager _daemonManager;
    private readonly HttpClient _httpClient;

    // Serialises every operation that writes or deletes .strm / .nfo files.
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly AlternateStreamStore _altStore;
    private readonly ConcurrentDictionary<string, byte> _warnedRoots = new(StringComparer.Ordinal);

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
        _altStore = new AlternateStreamStore(appPaths.DataPath, logger);
    }

    public static string FormatFileSize(long bytes)
    {
        if (bytes <= 0) return string.Empty;
        const double gb = 1024.0 * 1024.0 * 1024.0;
        const double mb = 1024.0 * 1024.0;
        if (bytes >= 1024L * 1024L * 1024L)
        {
            var val = bytes / gb;
            return $"{val:0.#} GB";
        }
        if (bytes >= 1024L * 1024L)
        {
            var val = bytes / mb;
            return $"{val:0.#} MB";
        }
        return $"{bytes / 1024.0:0.#} KB";
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

    // ------------------------------------------------------------------------------------------------------------
    // Category roots
    // ------------------------------------------------------------------------------------------------------------

    public string ResolveMediaRoot(string? targetFolder = null)
    {
        return ResolveMediaRootCore(targetFolder, createDirectories: true);
    }

    private string ResolveMediaRootCore(string? targetFolder, bool createDirectories)
    {
        // 0. If targetFolder is explicitly an absolute path specified by the user in CategoryMapping
        if (!string.IsNullOrWhiteSpace(targetFolder) && Path.IsPathRooted(targetFolder))
        {
            if (createDirectories)
            {
                try
                {
                    Directory.CreateDirectory(targetFolder);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not create category folder {Folder}", targetFolder);
                }
            }

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
        if (createDirectories)
        {
            Directory.CreateDirectory(defaultMedia);
        }

        return string.IsNullOrWhiteSpace(targetFolder) ? defaultMedia : Path.Combine(defaultMedia, targetFolder);
    }

    /// <summary>
    /// The configured categories, plus a built-in "Movies" / "Shows" category (folders "movies" / "shows" under the
    /// media root) when the configuration has none of that type: auto-routing always needs one of each.
    /// </summary>
    private static List<CategoryMapping> GetEffectiveCategories(PluginConfiguration config)
    {
        var categories = (config.Categories ?? new List<CategoryMapping>())
            .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Name))
            .ToList();

        var fallbacks = new[]
        {
            new CategoryMapping("Movies", "movies", "movie", new List<int>(), categories.Count == 0),
            new CategoryMapping("Shows", "shows", "show", new List<int>())
        };

        foreach (var fallback in fallbacks)
        {
            if (!categories.Any(c => string.Equals(c.Type, fallback.Type, StringComparison.OrdinalIgnoreCase)) &&
                !categories.Any(c => c.Name.Equals(fallback.Name, StringComparison.OrdinalIgnoreCase)))
            {
                categories.Add(fallback);
            }
        }

        return categories;
    }

    private List<CategoryRoot> GetCategoryRoots()
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var roots = new List<CategoryRoot>();

        foreach (var category in GetEffectiveCategories(config))
        {
            if (!TryResolveCategoryRoot(category, out var dir))
            {
                continue;
            }

            if (!roots.Any(r => r.Name.Equals(category.Name, StringComparison.OrdinalIgnoreCase) && PathEquals(r.Dir, dir)))
            {
                roots.Add(new CategoryRoot(category.Name, dir));
            }
        }

        return roots;
    }

    private bool TryResolveCategoryRoot(CategoryMapping category, out string dir)
    {
        dir = string.Empty;
        try
        {
            var folder = category.Folder;
            if (!string.IsNullOrWhiteSpace(folder) && !Path.IsPathRooted(folder) &&
                folder.Split('/', '\\').Any(segment => segment == ".."))
            {
                WarnRootOnce($"{category.Name}|{folder}", "Category '{Category}' has a folder with '..' segments ('{Folder}'); it is ignored", category.Name, folder);
                return false;
            }

            var canonical = Canonicalize(ResolveMediaRootCore(folder, createDirectories: false));
            if (canonical == null)
            {
                return false;
            }

            var unsafeReason = GetUnsafeRootReason(canonical);
            if (unsafeReason != null)
            {
                WarnRootOnce(canonical, "Category '{Category}' resolves to '{Folder}', which is not allowed as a category root ({Reason}); it is ignored", category.Name, canonical, unsafeReason);
                return false;
            }

            dir = canonical;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve the folder of category {Category}", category.Name);
            return false;
        }
    }

    /// <summary>
    /// The locations of all Jellyfin libraries, exactly as Jellyfin reports them (not yet validated).
    /// Any failure counts as "no libraries".
    /// </summary>
    private List<string> GetLibraryLocations()
    {
        var locations = new List<string>();
        try
        {
            var folders = _libraryManager.GetVirtualFolders();
            if (folders != null)
            {
                foreach (var folder in folders)
                {
                    foreach (var location in folder?.Locations ?? Array.Empty<string>())
                    {
                        if (!string.IsNullOrWhiteSpace(location))
                        {
                            locations.Add(location);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the Jellyfin library locations");
            return new List<string>();
        }

        return locations;
    }

    /// <summary>
    /// The canonical locations of all Jellyfin libraries, passed through the same safety filter as the category
    /// roots (rooted, no '..' segments, not a filesystem root, not containing Jellyfin's own directories) and
    /// deduplicated. These are used IN ADDITION to the category roots only for explicit, hash-targeted operations
    /// (delete one stream, delete one torrent's files, validate a target path); sync, orphan purge and the
    /// per-torrent duplicate cleanup never touch them.
    /// </summary>
    private List<CategoryRoot> GetLibraryRoots()
    {
        var roots = new List<CategoryRoot>();

        foreach (var location in GetLibraryLocations())
        {
            try
            {
                if (!Path.IsPathRooted(location) || location.Split('/', '\\').Any(segment => segment == ".."))
                {
                    WarnRootOnce("library|" + location, "Library location '{Location}' is not an absolute path without '..' segments; it is ignored", location);
                    continue;
                }

                var canonical = Canonicalize(location);
                if (canonical == null)
                {
                    continue;
                }

                var unsafeReason = GetUnsafeRootReason(canonical);
                if (unsafeReason != null)
                {
                    WarnRootOnce("library|" + canonical, "Library location '{Location}' is not allowed as a root for deleting streams ({Reason}); it is ignored", canonical, unsafeReason);
                    continue;
                }

                if (!roots.Any(r => PathEquals(r.Dir, canonical)))
                {
                    roots.Add(new CategoryRoot(LibraryRootName, canonical));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not resolve the Jellyfin library location {Location}", location);
            }
        }

        return roots;
    }

    /// <summary>
    /// Category roots plus library roots (a library root equal to a category root is listed once, as the category).
    /// Only for explicit, hash-targeted operations on a specific torrent or item directory; bulk operations must keep
    /// using <see cref="GetCategoryRoots"/>.
    /// </summary>
    private List<CategoryRoot> GetManagedRoots(IReadOnlyList<CategoryRoot>? categoryRoots = null)
    {
        var roots = new List<CategoryRoot>(categoryRoots ?? GetCategoryRoots());
        foreach (var libraryRoot in GetLibraryRoots())
        {
            if (!roots.Any(r => PathEquals(r.Dir, libraryRoot.Dir)))
            {
                roots.Add(libraryRoot);
            }
        }

        return roots;
    }

    private void WarnRootOnce(string key, string message, params object[] args)
    {
        if (_warnedRoots.TryAdd(key, 0))
        {
            _logger.LogWarning(message, args);
        }
    }

    /// <summary>
    /// A category root must never be a filesystem root, and must never contain Jellyfin's own data, config,
    /// cache, log or plugin directories (e.g. a category folder of "/config").
    /// </summary>
    private string? GetUnsafeRootReason(string canonicalRoot)
    {
        var fsRoot = Path.GetPathRoot(canonicalRoot);
        if (!string.IsNullOrEmpty(fsRoot) && PathEquals(fsRoot, canonicalRoot))
        {
            return "filesystem root";
        }

        var protectedPaths = new (string Label, string? Dir)[]
        {
            ("program data", _appPaths.ProgramDataPath),
            ("data", _appPaths.DataPath),
            ("configuration", _appPaths.ConfigurationDirectoryPath),
            ("cache", _appPaths.CachePath),
            ("log", _appPaths.LogDirectoryPath),
            ("plugins", _appPaths.PluginsPath)
        };

        foreach (var (label, dir) in protectedPaths)
        {
            var canonicalProtected = Canonicalize(dir);
            if (canonicalProtected != null && IsInsideOrEqual(canonicalRoot, canonicalProtected))
            {
                return $"it contains the Jellyfin {label} directory";
            }
        }

        return null;
    }

    private static string? Canonicalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(path);
            var fsRoot = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(fsRoot) && string.Equals(full, fsRoot, PathComparison))
            {
                return full;
            }

            return Path.TrimEndingDirectorySeparator(full);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool PathEquals(string a, string b) => string.Equals(a, b, PathComparison);

    /// <summary>True when <paramref name="path"/> is <paramref name="dir"/> or lies inside it. Both must be canonical.</summary>
    private static bool IsInsideOrEqual(string dir, string path)
    {
        if (PathEquals(dir, path))
        {
            return true;
        }

        var prefix = dir.EndsWith(Path.DirectorySeparatorChar) || dir.EndsWith(Path.AltDirectorySeparatorChar)
            ? dir
            : dir + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }

    private static CategoryRoot? FindContainingRoot(IReadOnlyList<CategoryRoot> roots, string canonicalPath)
    {
        CategoryRoot? best = null;
        foreach (var root in roots)
        {
            if (IsInsideOrEqual(root.Dir, canonicalPath) && (best == null || root.Dir.Length > best.Dir.Length))
            {
                best = root;
            }
        }

        return best;
    }

    private static bool IsInsideAnyRoot(IReadOnlyList<CategoryRoot> roots, string path)
    {
        var canonical = Canonicalize(path);
        return canonical != null && FindContainingRoot(roots, canonical) != null;
    }

    /// <summary>Returns the configured category whose resolved root contains <paramref name="path"/> (longest root wins).</summary>
    public string? GetCategoryNameForPath(string path)
    {
        return GetCategoryNameForPath(path, GetCategoryRoots());
    }

    private static string? GetCategoryNameForPath(string path, IReadOnlyList<CategoryRoot> roots)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            return null;
        }

        var canonical = Canonicalize(path);
        return canonical == null ? null : FindContainingRoot(roots, canonical)?.Name;
    }

    /// <summary>
    /// True when <paramref name="path"/> is a category root or a Jellyfin library root, or lies inside one (after
    /// canonicalisation). Used to validate explicit, user-chosen targets; it is not a licence for bulk operations.
    /// </summary>
    public bool IsPathInsideManagedRoots(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            return false;
        }

        return IsInsideAnyRoot(GetManagedRoots(), path);
    }

    /// <summary>Category roots to enumerate: distinct, with roots nested inside another root left out.</summary>
    private static List<string> GetScanRoots(IReadOnlyList<CategoryRoot> roots)
    {
        var dirs = roots.Select(r => r.Dir).Distinct(PathComparer).ToList();
        return dirs.Where(d => !dirs.Any(o => !PathEquals(o, d) && IsInsideOrEqual(o, d))).ToList();
    }

    // ------------------------------------------------------------------------------------------------------------
    // .strm index
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Maps torrent hash (lowercase) to every .strm path in all category roots whose URL carries that hash.</summary>
    public Dictionary<string, List<string>> BuildStrmIndex()
    {
        return BuildStrmIndexCore(GetCategoryRoots());
    }

    private Dictionary<string, List<string>> BuildStrmIndexCore(IReadOnlyList<CategoryRoot> roots)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(PathComparer);

        foreach (var root in GetScanRoots(roots))
        {
            try
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(root, "*.strm", StrmTreeEnumeration))
                {
                    if (!seen.Add(file))
                    {
                        continue;
                    }

                    var hash = TryReadStrmHash(file);
                    if (hash == null)
                    {
                        continue;
                    }

                    if (!index.TryGetValue(hash, out var list))
                    {
                        index[hash] = list = new List<string>();
                    }

                    list.Add(file);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not enumerate .strm files in {Root}", root);
            }
        }

        return index;
    }

    private static string? TryReadStrmText(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[MaxStrmReadChars];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            return new string(buffer, 0, read);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? TryReadStrmHash(string path)
    {
        var text = TryReadStrmText(path);
        return text != null && TorrentHash.TryExtractFromStrm(text, out var hash) ? hash : null;
    }

    private static bool FileOwnedBy(string strmPath, string hash)
    {
        var owner = TryReadStrmHash(strmPath);
        return owner != null && TorrentHash.Equal(owner, hash);
    }

    /// <summary>True when the file exists and is not a .strm of <paramref name="hash"/> (another torrent's, or not ours at all).</summary>
    private static bool IsOwnedByAnotherTorrent(string path, string hash)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var text = TryReadStrmText(path);
        if (text == null)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            // empty leftover of an interrupted write: ours to replace
            return false;
        }

        if (IsPlaceholderText(text))
        {
            // a movie placeholder belongs to no torrent: the real stream takes its place
            return false;
        }

        return !(TorrentHash.TryExtractFromStrm(text, out var owner) && TorrentHash.Equal(owner, hash));
    }

    /// <summary>True when <paramref name="text"/> (the content of a .strm) contains <see cref="PlaceholderMarker"/>.</summary>
    public static bool IsPlaceholderStrmText(string? text)
    {
        return !string.IsNullOrEmpty(text) && text.Contains(PlaceholderMarker, StringComparison.OrdinalIgnoreCase);
    }

    // A placeholder as far as this engine is concerned: the marker and no torrent hash. A file that names a torrent
    // is a real stream whatever else it says.
    private static bool IsPlaceholderText(string? text)
    {
        return IsPlaceholderStrmText(text) && !TorrentHash.TryExtractFromStrm(text, out _);
    }

    private static bool IsPlaceholderStrmFile(string path)
    {
        return IsPlaceholderText(TryReadStrmText(path));
    }

    /// <summary>
    /// True when a .strm directly in <paramref name="dir"/> is not a placeholder: a real stream (or any other .strm,
    /// including one that cannot be read), which a placeholder must never take the place of.
    /// </summary>
    private static bool HasNonPlaceholderStrm(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.strm", StrmTopLevelEnumeration).Any(f => !IsPlaceholderStrmFile(f));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The placeholder .strm files directly in <paramref name="dir"/>, in ordinal name order.</summary>
    private static List<string> FindPlaceholderStrms(string dir)
    {
        var found = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.strm", StrmTopLevelEnumeration).OrderBy(f => f, StringComparer.Ordinal))
            {
                if (IsPlaceholderStrmFile(file))
                {
                    found.Add(file);
                }
            }
        }
        catch (Exception)
        {
            // unreadable folder: no placeholders
        }

        return found;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Sync
    // ------------------------------------------------------------------------------------------------------------

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
        ArgumentNullException.ThrowIfNull(torrent);

        if (!TorrentHash.IsValid(TorrentHash.Normalize(torrent.Hash ?? string.Empty)))
        {
            return RejectInvalidHash(torrent);
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var roots = GetCategoryRoots();
            var index = BuildStrmIndexCore(roots);
            return await SyncTorrentCoreAsync(
                torrent, category, customTitle, posterUrl, tmdbId, imdbId, forceCategory, roots, index, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Syncs every torrent of a list that TorrServer returned successfully, using each torrent's stored category,
    /// then (when <paramref name="purgeOrphans"/> is set and the list is not empty) removes the .strm files of
    /// torrents that no longer exist. Holds the engine lock for the whole run.
    /// </summary>
    public async Task<FullSyncReport> FullSyncAsync(
        IReadOnlyList<TorrTorrent> torrents,
        bool purgeOrphans,
        CancellationToken ct = default,
        IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(torrents);

        await _lock.WaitAsync(ct);
        try
        {
            return await FullSyncCoreAsync(torrents, purgeOrphans, progress, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<FullSyncReport> FullSyncCoreAsync(
        IReadOnlyList<TorrTorrent> torrents,
        bool purgeOrphans,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var report = new FullSyncReport();

        if (torrents.Count == 0)
        {
            _logger.LogWarning("Full sync was given an empty torrent list; nothing to sync and no orphans will be purged");
            progress?.Report(100);
            return report;
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var categories = GetEffectiveCategories(config);
        var roots = GetCategoryRoots();
        var index = BuildStrmIndexCore(roots);

        var migrated = MigrateLegacyAlternates(index);
        if (migrated > 0)
        {
            _logger.LogInformation("Registered {Count} existing alternate stream version(s) so syncing leaves them alone", migrated);
        }

        var activeHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var done = 0;

        foreach (var torrent in torrents)
        {
            ct.ThrowIfCancellationRequested();
            done++;

            var hash = TorrentHash.Normalize(torrent?.Hash ?? string.Empty);
            if (torrent == null || !TorrentHash.IsValid(hash))
            {
                report.Failed++;
                AddReportError(report, $"{Truncate(torrent?.Hash, 64)}: invalid torrent hash");
                progress?.Report(done * 100.0 / torrents.Count);
                continue;
            }

            if (!activeHashes.Add(hash))
            {
                continue;
            }

            string? category = null;
            var force = false;
            if (!string.IsNullOrWhiteSpace(torrent.Category))
            {
                var configured = categories.FirstOrDefault(c => c.Name.Equals(torrent.Category, StringComparison.OrdinalIgnoreCase));
                if (configured != null)
                {
                    category = configured.Name;
                    force = true;
                }
            }

            var result = await SyncTorrentCoreAsync(torrent, category, null, null, null, null, force, roots, index, ct);
            if (result.Success)
            {
                report.Synced++;
            }
            else if (string.Equals(result.ErrorMessage, MetadataNotReadyMessage, StringComparison.Ordinal))
            {
                report.SkippedNotReady++;
            }
            else
            {
                report.Failed++;
                AddReportError(report, $"{hash}: {result.ErrorMessage}");
            }

            progress?.Report(done * 100.0 / torrents.Count);
        }

        if (purgeOrphans)
        {
            // torrents.Count > 0 is guaranteed here (the empty list returned above).
            report.OrphansDeleted = PurgeOrphans(index, activeHashes, roots, ct);
            var forgotten = _altStore.RemoveWhere(h => !activeHashes.Contains(h));
            if (forgotten > 0)
            {
                _logger.LogInformation("Forgot {Count} alternate stream entries of torrents that no longer exist", forgotten);
            }
        }

        _logger.LogInformation(
            "Full sync finished: {Synced} synced, {Failed} failed, {NotReady} waiting for metadata, {Orphans} orphaned .strm files deleted",
            report.Synced, report.Failed, report.SkippedNotReady, report.OrphansDeleted);

        progress?.Report(100);
        return report;
    }

    private static void AddReportError(FullSyncReport report, string message)
    {
        if (report.Errors.Count < MaxReportedErrors)
        {
            report.Errors.Add(message);
        }
    }

    private StrmSyncResult RejectInvalidHash(TorrTorrent torrent)
    {
        _logger.LogWarning("Refusing to sync a torrent with an invalid hash: '{Hash}'", Truncate(torrent.Hash, 64));
        return new StrmSyncResult
        {
            TorrentHash = torrent.Hash ?? string.Empty,
            TorrentTitle = torrent.Title ?? string.Empty,
            Success = false,
            ErrorMessage = "invalid torrent hash"
        };
    }

    private async Task<StrmSyncResult> SyncTorrentCoreAsync(
        TorrTorrent torrent,
        string? category,
        string? customTitle,
        string? posterUrl,
        int? tmdbId,
        string? imdbId,
        bool forceCategory,
        IReadOnlyList<CategoryRoot> roots,
        Dictionary<string, List<string>> index,
        CancellationToken ct)
    {
        var hash = TorrentHash.Normalize(torrent.Hash ?? string.Empty);
        if (!TorrentHash.IsValid(hash))
        {
            return RejectInvalidHash(torrent);
        }

        var result = new StrmSyncResult
        {
            TorrentHash = hash,
            TorrentTitle = torrent.Title ?? string.Empty
        };

        try
        {
            if (_altStore.TryGet(hash, out var alternate) && Directory.Exists(alternate.TargetDir))
            {
                if (alternate.IsSeries)
                {
                    // A release merged into an existing series: keep its episodes in that series folder (idempotent)
                    // instead of creating a standalone folder, and skip the standard path and its cleanup. When the
                    // folder can no longer be used, fall through to the standard path like an unregistered torrent.
                    if (await TrySyncRegisteredSeriesAsync(torrent, hash, alternate, roots, result, ct))
                    {
                        return result;
                    }
                }
                else
                {
                    // An alternate version lives inside its item's folder; syncing it would create a standalone folder
                    // and then delete the alternate from the item it belongs to.
                    result.Success = true;
                    result.FolderPath = alternate.TargetDir;
                    result.Category = GetCategoryNameForPath(alternate.TargetDir, roots) ?? string.Empty;
                    _logger.LogDebug("Torrent {Hash} is an alternate stream of {Dir}; skipping standalone sync", hash, alternate.TargetDir);
                    return result;
                }
            }

            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var rawTitle = !string.IsNullOrWhiteSpace(customTitle) ? customTitle : torrent.Title ?? string.Empty;
            var (cleanTitle, originalTitle, extractedYear) = TitleCleaner.CleanTitleAndExtractYear(rawTitle);

            var categories = GetEffectiveCategories(config);

            // Determine initial category
            var chosenCat = categories.FirstOrDefault(c => c.Name.Equals(category, StringComparison.OrdinalIgnoreCase))
                ?? categories.FirstOrDefault(c => c.IsDefault)
                ?? categories.FirstOrDefault(c => string.Equals(c.Type, "movie", StringComparison.OrdinalIgnoreCase))
                ?? categories[0];

            var files = torrent.GetEffectiveFiles();
            var videoFiles = files.Where(f => EpisodeMatcher.IsVideoFile(f.Path)).ToList();
            var isPlaceholder = false;

            if (files.Count == 0)
            {
                // TorrServer has not loaded the file list yet.
                if (HasExistingStrm(hash, index))
                {
                    // Real files may already be on disk: never replace them with a placeholder, never clean up.
                    _logger.LogDebug("Metadata of torrent {Hash} is not ready; leaving its existing .strm files untouched", hash);
                    result.ErrorMessage = MetadataNotReadyMessage;
                    return result;
                }

                // First add from search: write a single-file placeholder so the item shows up instantly.
                videoFiles.Add(new TorrTorrentFile { Id = 1, Path = $"{cleanTitle}.mkv", Length = torrent.TorrentSize });
                isPlaceholder = true;
            }
            else if (videoFiles.Count == 0)
            {
                // No recognisable video extension: treat the first file as the video.
                videoFiles.Add(files[0]);
            }

            result.VideoFilesCount = videoFiles.Count;

            // Check if chosen category is explicitly a TV show or Movie category
            var isChosenCatShow = string.Equals(chosenCat.Type, "show", StringComparison.OrdinalIgnoreCase);
            var isChosenCatMovie = string.Equals(chosenCat.Type, "movie", StringComparison.OrdinalIgnoreCase);

            // Detect if this is a TV series release based on show title keywords or episode tags
            var isDetectedShow = EpisodeMatcher.IsShowRelease(rawTitle, videoFiles.Select(f => f.Path));

            // Auto-route to Show category ONLY IF category was not explicitly specified and torrent is a detected show
            if (!forceCategory && string.IsNullOrWhiteSpace(category) && isDetectedShow && isChosenCatMovie)
            {
                var showCat = categories.FirstOrDefault(c => string.Equals(c.Type, "show", StringComparison.OrdinalIgnoreCase));
                if (showCat != null)
                {
                    _logger.LogInformation("Torrent '{Title}' detected as a TV series. Auto-routing from '{OldCategory}' to '{NewCategory}'", rawTitle, chosenCat.Name, showCat.Name);
                    chosenCat = showCat;
                    isChosenCatShow = true;
                }
            }

            // Treat as TV show ONLY if the category is a show category (or auto-routed to a show category)
            var isShow = isChosenCatShow;
            result.Category = chosenCat.Name;

            // The category folder must be one of the (safe) managed roots; otherwise nothing is written.
            if (!TryResolveCategoryRoot(chosenCat, out var categoryDir) || !roots.Any(r => PathEquals(r.Dir, categoryDir)))
            {
                _logger.LogWarning("Category '{Category}' has no permitted root folder; not syncing torrent {Hash}", chosenCat.Name, hash);
                result.ErrorMessage = $"Category '{chosenCat.Name}' has no permitted root folder.";
                return result;
            }

            Directory.CreateDirectory(categoryDir);

            var changed = isShow
                ? await SyncShowAsync(config, hash, rawTitle, cleanTitle, originalTitle, extractedYear, categoryDir, videoFiles, posterUrl, tmdbId, imdbId, result, ct)
                : await SyncMovieAsync(config, hash, rawTitle, cleanTitle, originalTitle, extractedYear, categoryDir, videoFiles, posterUrl, tmdbId, imdbId, result, ct);

            result.Success = true;

            if (changed)
            {
                _logger.LogInformation("Wrote {Count} .strm files for '{Title}' in {Dir}", result.CreatedStrmFiles.Count, cleanTitle, result.FolderPath);

                // Fast targeted scan notification
                NotifyPathChanged(result.FolderPath);
            }
            else
            {
                _logger.LogDebug("STRM files for '{Title}' in {Dir} are already up to date", cleanTitle, result.FolderPath);
            }

            // Remove obsolete/legacy .strm duplicates of this torrent. Never with a placeholder, and never when
            // nothing was written (that would delete every .strm of the torrent).
            if (!isPlaceholder && result.CreatedStrmFiles.Count > 0)
            {
                CleanupObsoleteCore(hash, result.CreatedStrmFiles, roots, index, ct);
            }

            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync STRM files for torrent {Hash}", hash);
            result.Success = false;
            result.ErrorMessage = "Failed to write STRM files; see the Jellyfin log for details.";
            return result;
        }
    }

    private static bool HasExistingStrm(string hash, Dictionary<string, List<string>> index)
    {
        return index.TryGetValue(hash, out var paths) && paths.Any(File.Exists);
    }

    private async Task<bool> SyncMovieAsync(
        PluginConfiguration config,
        string hash,
        string rawTitle,
        string cleanTitle,
        string? originalTitle,
        int? extractedYear,
        string categoryDir,
        List<TorrTorrentFile> videoFiles,
        string? posterUrl,
        int? tmdbId,
        string? imdbId,
        StrmSyncResult result,
        CancellationToken ct)
    {
        var changed = false;

        // Movies / Movie Collections
        var fileTemplate = !string.IsNullOrWhiteSpace(config.MovieFileTemplate) ? config.MovieFileTemplate : "{Title} ({Year}).strm";

        // Ensure file and folder names on disk are always in English/Latin
        var fileTitle = TitleCleaner.GetEnglishOrLatinTitle(cleanTitle, originalTitle);

        var tokens = BuildMovieTokens(fileTitle, originalTitle, extractedYear, rawTitle);

        var folderName = ResolveMovieFolderName(config, tokens, rawTitle, fileTitle, extractedYear, hash);
        var movieDir = Path.Combine(categoryDir, folderName);
        Directory.CreateDirectory(movieDir);
        result.FolderPath = movieDir;

        var usedPaths = new HashSet<string>(PathComparer);
        var streamBase = _daemonManager.GetStreamBaseUrl();

        if (videoFiles.Count == 1)
        {
            // Single movie file
            var mainFile = videoFiles[0];
            var strmName = BuildSingleMovieStrmName(config, tokens, rawTitle, mainFile.Path, folderName, hash);
            var strmPath = ResolveStrmPath(movieDir, strmName, mainFile.Id, hash, usedPaths);
            var streamUrl = $"{streamBase}/stream?link={hash}&index={mainFile.Id}&play\n";
            changed |= await WriteTextIfChangedAsync(strmPath, streamUrl, ct);
            result.CreatedStrmFiles.Add(strmPath);

            // Write Movie NFO (so Jellyfin matches reliably)
            changed |= await WriteMovieNfoAsync(movieDir, folderName, cleanTitle, originalTitle, extractedYear, tmdbId, imdbId, createOnly: false, ct);
        }
        else
        {
            // Multi-movie collection or multi-part movie in a movie library
            foreach (var videoFile in videoFiles.OrderBy(f => f.Path, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();

                var fileBase = GetBaseName(videoFile.Path);
                string stemRaw;
                var wholeName = false;
                if (config.EnableMovieRenaming)
                {
                    var (itemCleanTitle, itemOrigTitle, itemYear) = TitleCleaner.CleanTitleAndExtractYear(fileBase);
                    if (!string.IsNullOrWhiteSpace(itemCleanTitle) && itemCleanTitle.Length > 2)
                    {
                        var itemFileTitle = TitleCleaner.GetEnglishOrLatinTitle(itemCleanTitle, itemOrigTitle);
                        var itemYearToken = itemYear.HasValue && !itemFileTitle.Contains(itemYear.Value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                            ? itemYear.Value.ToString(CultureInfo.InvariantCulture)
                            : string.Empty;

                        var itemTokens = new Dictionary<string, string>
                        {
                            ["Title"] = itemFileTitle,
                            ["OriginalTitle"] = itemOrigTitle ?? itemFileTitle,
                            ["Year"] = itemYearToken,
                            ["RawTitle"] = fileBase
                        };
                        stemRaw = FormatTemplate(fileTemplate, itemTokens);
                        wholeName = true;
                    }
                    else
                    {
                        stemRaw = fileBase;
                    }
                }
                else
                {
                    stemRaw = string.IsNullOrWhiteSpace(fileBase) ? $"Movie_{videoFile.Id}" : fileBase;
                }

                var strmName = BuildStrmFileName(stemRaw, $"Movie_{videoFile.Id}", hash, wholeName);
                var strmPath = ResolveStrmPath(movieDir, strmName, videoFile.Id, hash, usedPaths);
                var streamUrl = $"{streamBase}/stream?link={hash}&index={videoFile.Id}&play\n";
                changed |= await WriteTextIfChangedAsync(strmPath, streamUrl, ct);
                result.CreatedStrmFiles.Add(strmPath);

                // Per-movie NFO so Jellyfin can identify each one individually. The torrent-level tmdb/imdb ids
                // belong to the collection as a whole, so they are deliberately not written here.
                var itemBaseWithoutExt = Path.GetFileNameWithoutExtension(strmPath);
                var (partTitle, partOrig, partYear) = TitleCleaner.CleanTitleAndExtractYear(itemBaseWithoutExt);
                changed |= await WriteMovieItemNfoAsync(movieDir, itemBaseWithoutExt, partTitle, partOrig, partYear ?? extractedYear, ct);
            }
        }

        // The real stream takes the place of a movie placeholder that sits in this folder under another file name
        // (one with the same name was overwritten above).
        changed |= DeletePlaceholderStrms(movieDir, result.CreatedStrmFiles);

        // Download poster if provided
        if (!string.IsNullOrWhiteSpace(posterUrl))
        {
            changed |= await DownloadPosterAsync(movieDir, posterUrl, ct);
        }

        return changed;
    }

    /// <summary>
    /// Deletes the movie placeholder .strm files (marker, no torrent hash) directly in <paramref name="dir"/> that are
    /// not in <paramref name="keep"/>. Nothing else is touched, not even a placeholder's sidecar .nfo. Returns true
    /// when a file was deleted. The caller guarantees that <paramref name="dir"/> lies inside a managed root.
    /// </summary>
    private bool DeletePlaceholderStrms(string dir, IReadOnlyCollection<string> keep)
    {
        var deleted = false;
        try
        {
            var kept = new HashSet<string>(keep.Select(p => Canonicalize(p) ?? p), PathComparer);
            foreach (var file in Directory.EnumerateFiles(dir, "*.strm", StrmTopLevelEnumeration).ToList())
            {
                if (kept.Contains(Canonicalize(file) ?? file) || !IsPlaceholderStrmFile(file))
                {
                    continue;
                }

                try
                {
                    File.Delete(file);
                    deleted = true;
                    _logger.LogInformation("Removed movie placeholder {File}: a real stream took its place", file);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete movie placeholder {File}", file);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not look for movie placeholders in {Dir}", dir);
        }

        return deleted;
    }

    private async Task<bool> SyncShowAsync(
        PluginConfiguration config,
        string hash,
        string rawTitle,
        string cleanTitle,
        string? originalTitle,
        int? extractedYear,
        string categoryDir,
        List<TorrTorrentFile> videoFiles,
        string? posterUrl,
        int? tmdbId,
        string? imdbId,
        StrmSyncResult result,
        CancellationToken ct)
    {
        var changed = false;

        // Multi-episode series
        var showFolderTemplate = !string.IsNullOrWhiteSpace(config.ShowFolderTemplate) ? config.ShowFolderTemplate : "{Title} ({Year})";

        // Ensure file and folder names on disk are always in English/Latin
        var showFileTitle = TitleCleaner.GetEnglishOrLatinTitle(cleanTitle, originalTitle);

        var showTokens = BuildShowTokens(showFileTitle, originalTitle, extractedYear, rawTitle);

        var seriesFolder = ResolveFolderName(config.EnableShowRenaming, showFolderTemplate, showTokens, rawTitle, showFileTitle, extractedYear, hash);
        var seriesDir = Path.Combine(categoryDir, seriesFolder);
        Directory.CreateDirectory(seriesDir);
        result.FolderPath = seriesDir;

        changed |= await WriteShowEpisodesAsync(config, hash, showTokens, seriesDir, videoFiles, result.CreatedStrmFiles, tracker: null, ct);

        // Write Series tvshow.nfo so Jellyfin identifies the series properly
        changed |= await WriteShowNfoAsync(seriesDir, cleanTitle, originalTitle, extractedYear, tmdbId, imdbId, ct);

        // Download poster
        if (!string.IsNullOrWhiteSpace(posterUrl))
        {
            changed |= await DownloadPosterAsync(seriesDir, posterUrl, ct);
        }

        return changed;
    }

    /// <summary>
    /// The tokens a show's folder / season / episode templates are filled from. The year token stays empty when the
    /// title already contains the year (no "Show 2008 (2008)").
    /// </summary>
    private static Dictionary<string, string> BuildShowTokens(string showFileTitle, string? originalTitle, int? year, string rawTitle)
    {
        // Avoid duplicating year in show folder name if showFileTitle already contains the year
        var showYearToken = year.HasValue && !showFileTitle.Contains(year.Value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            ? year.Value.ToString(CultureInfo.InvariantCulture)
            : string.Empty;

        return new Dictionary<string, string>
        {
            ["Title"] = showFileTitle,
            ["OriginalTitle"] = originalTitle ?? showFileTitle,
            ["Year"] = showYearToken,
            ["RawTitle"] = rawTitle
        };
    }

    /// <summary>
    /// Writes one .strm per video file into <paramref name="seriesDir"/>, laid out by season
    /// (season folder / episode file templates, collision and foreign-file rules, write-if-changed). Shared by the
    /// standalone show sync and by <see cref="AddTorrentToSeriesAsync"/>, which differ only in how the series folder
    /// and the tokens are chosen. Appends the path of every .strm in place to <paramref name="writtenPaths"/> and
    /// returns whether anything was written.
    /// </summary>
    private async Task<bool> WriteShowEpisodesAsync(
        PluginConfiguration config,
        string hash,
        Dictionary<string, string> showTokens,
        string seriesDir,
        List<TorrTorrentFile> videoFiles,
        List<string> writtenPaths,
        WriteTracker? tracker,
        CancellationToken ct)
    {
        var changed = false;

        var seasonFolderTemplate = !string.IsNullOrWhiteSpace(config.ShowSeasonFolderTemplate) ? config.ShowSeasonFolderTemplate : "Season {Season:00}";
        var epFileTemplate = !string.IsNullOrWhiteSpace(config.ShowEpisodeFileTemplate) ? config.ShowEpisodeFileTemplate : "{Title} - {EpisodeTag}.strm";

        // Ordinal order, and a fallback episode counter that is independent per season.
        var ordered = videoFiles.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        var episodeInfos = ordered.Select(f => EpisodeMatcher.ParseEpisodeInfo(f.Path, 0)).ToList();
        EpisodeMatcher.AssignFallbackEpisodeNumbers(episodeInfos);

        var usedPaths = new HashSet<string>(PathComparer);
        var streamBase = _daemonManager.GetStreamBaseUrl();

        for (var i = 0; i < ordered.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var videoFile = ordered[i];
            var epInfo = episodeInfos[i];

            var seasonTokens = new Dictionary<string, string>(showTokens)
            {
                ["Season"] = epInfo.SeasonNumber.ToString(CultureInfo.InvariantCulture),
                ["Season:00"] = epInfo.SeasonNumber.ToString("D2", CultureInfo.InvariantCulture),
                ["SeasonFolder"] = epInfo.FormatSeasonFolder()
            };

            var seasonFolderName = TitleCleaner.SanitizeFilenameOrEmpty(FormatTemplate(seasonFolderTemplate, seasonTokens));
            if (seasonFolderName.Length == 0)
            {
                seasonFolderName = epInfo.FormatSeasonFolder();
            }

            var seasonDir = Path.Combine(seriesDir, seasonFolderName);
            tracker?.NoteDirectory(seasonDir);
            Directory.CreateDirectory(seasonDir);

            var epTag = epInfo.FormatEpisodeTag();
            var epTokens = new Dictionary<string, string>(seasonTokens)
            {
                ["Episode"] = epInfo.EpisodeNumber.ToString(CultureInfo.InvariantCulture),
                ["Episode:00"] = epInfo.EpisodeNumber.ToString("D2", CultureInfo.InvariantCulture),
                ["EpisodeTag"] = epTag
            };

            string stemRaw;
            if (config.EnableShowRenaming)
            {
                stemRaw = FormatTemplate(epFileTemplate, epTokens);
            }
            else
            {
                var origEpBase = GetBaseName(videoFile.Path);
                stemRaw = string.IsNullOrWhiteSpace(origEpBase)
                    ? $"Episode {epInfo.EpisodeNumber.ToString("D2", CultureInfo.InvariantCulture)}"
                    : origEpBase;
            }

            var strmFileName = BuildStrmFileName(stemRaw, epTag, hash, wholeNameSanitized: true);
            var strmPath = ResolveStrmPath(seasonDir, strmFileName, videoFile.Id, hash, usedPaths);
            var streamUrl = $"{streamBase}/stream?link={hash}&index={videoFile.Id}&play\n";
            tracker?.NoteFile(strmPath);
            changed |= await WriteTextIfChangedAsync(strmPath, streamUrl, ct);
            writtenPaths.Add(strmPath);
        }

        return changed;
    }

    /// <summary>
    /// Remembers which files and directories a write run created (as opposed to found), so a run that fails halfway
    /// can take back exactly what it added.
    /// </summary>
    private sealed class WriteTracker
    {
        public List<string> CreatedFiles { get; } = new();

        public List<string> CreatedDirectories { get; } = new();

        public void NoteFile(string path)
        {
            if (!File.Exists(path) && !CreatedFiles.Contains(path, PathComparer))
            {
                CreatedFiles.Add(path);
            }
        }

        public void NoteDirectory(string path)
        {
            if (!Directory.Exists(path) && !CreatedDirectories.Contains(path, PathComparer))
            {
                CreatedDirectories.Add(path);
            }
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Naming helpers
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The tokens a movie's folder / file templates are filled from (shared by the torrent sync and by placeholders,
    /// so both land on the same names). The year token stays empty when the title already contains the year.
    /// </summary>
    private static Dictionary<string, string> BuildMovieTokens(string fileTitle, string? originalTitle, int? year, string rawTitle)
    {
        // Avoid duplicating year in folder name if fileTitle already contains the year
        var yearToken = year.HasValue && !fileTitle.Contains(year.Value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            ? year.Value.ToString(CultureInfo.InvariantCulture)
            : string.Empty;

        return new Dictionary<string, string>
        {
            ["Title"] = fileTitle,
            ["OriginalTitle"] = originalTitle ?? fileTitle,
            ["Year"] = yearToken,
            ["RawTitle"] = rawTitle
        };
    }

    private static string ResolveMovieFolderName(
        PluginConfiguration config,
        Dictionary<string, string> tokens,
        string rawTitle,
        string fileTitle,
        int? year,
        string hash)
    {
        var folderTemplate = !string.IsNullOrWhiteSpace(config.MovieFolderTemplate) ? config.MovieFolderTemplate : "{Title} ({Year})";
        return ResolveFolderName(config.EnableMovieRenaming, folderTemplate, tokens, rawTitle, fileTitle, year, hash);
    }

    /// <summary>The .strm file name of a single-video movie (the primary file of its folder).</summary>
    private static string BuildSingleMovieStrmName(
        PluginConfiguration config,
        Dictionary<string, string> tokens,
        string rawTitle,
        string? videoPath,
        string folderName,
        string hash)
    {
        string stemRaw;
        if (config.EnableMovieRenaming)
        {
            var fileTemplate = !string.IsNullOrWhiteSpace(config.MovieFileTemplate) ? config.MovieFileTemplate : "{Title} ({Year}).strm";
            stemRaw = FormatTemplate(fileTemplate, tokens);
        }
        else
        {
            var origBase = GetBaseName(videoPath);
            stemRaw = string.IsNullOrWhiteSpace(origBase) ? rawTitle : origBase;
        }

        return BuildStrmFileName(stemRaw, folderName, hash, wholeNameSanitized: config.EnableMovieRenaming);
    }

    private static string ResolveFolderName(
        bool renaming,
        string template,
        Dictionary<string, string> tokens,
        string rawTitle,
        string fileTitle,
        int? year,
        string hash)
    {
        string name;
        if (renaming)
        {
            name = TitleCleaner.SanitizeFilenameOrEmpty(FormatTemplate(template, tokens));
            if (name.Length == 0)
            {
                name = TitleCleaner.SanitizeFilenameOrEmpty(TitleCleaner.FormatFolderTitle(fileTitle, year));
            }
        }
        else
        {
            name = TitleCleaner.SanitizeFilenameOrEmpty(rawTitle);
        }

        if (name.Length == 0)
        {
            name = TitleCleaner.SanitizeFilenameOrEmpty(fileTitle);
        }

        return name.Length > 0 ? name : TitleCleaner.FallbackName(hash);
    }

    /// <summary>
    /// Builds "{stem}.strm" from a template or file-name result: sanitised, length-capped, never empty.
    /// <paramref name="wholeNameSanitized"/> keeps the historic behaviour of the template paths, where the whole
    /// name including ".strm" was sanitised (so "Title .strm" stays "Title .strm"); the file-name paths sanitised
    /// the stem first and appended ".strm" afterwards.
    /// </summary>
    private static string BuildStrmFileName(string rawName, string? fallbackStem, string hash, bool wholeNameSanitized)
    {
        var stemRaw = rawName ?? string.Empty;
        if (stemRaw.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        {
            stemRaw = stemRaw[..^5];
        }

        if (TitleCleaner.SanitizeFilenameOrEmpty(stemRaw).Length == 0)
        {
            // Nothing usable in the name: fall back to a safe stem instead of writing ".strm" / "strm".
            var fallback = TitleCleaner.SanitizeFilenameOrEmpty(fallbackStem);
            if (fallback.Length == 0)
            {
                fallback = TitleCleaner.FallbackName(hash);
            }

            return TitleCleaner.SanitizeFilename(fallback + ".strm", hash);
        }

        return wholeNameSanitized
            ? TitleCleaner.SanitizeFilename(stemRaw + ".strm", hash)
            : TitleCleaner.SanitizeFilename(TitleCleaner.SanitizeFilenameOrEmpty(stemRaw) + ".strm", hash);
    }

    private static string GetBaseName(string? path)
    {
        var normalized = (path ?? string.Empty).Replace('\\', '/');
        var name = normalized[(normalized.LastIndexOf('/') + 1)..];
        return Path.GetFileNameWithoutExtension(name);
    }

    private static string InsertBeforeExtension(string fileName, string suffix)
    {
        var ext = Path.GetExtension(fileName);
        return fileName[..^ext.Length] + suffix + ext;
    }

    /// <summary>
    /// Picks the path of a .strm to write. A name already used by another video file of the same sync, or already
    /// owned by a different torrent (or by a file that is not ours), is disambiguated instead of being overwritten.
    /// </summary>
    private string ResolveStrmPath(string dir, string strmFileName, int fileId, string hash, HashSet<string> usedPaths)
    {
        var first = Path.Combine(dir, strmFileName);
        var sameSyncCollision = usedPaths.Contains(first);
        if (!sameSyncCollision && !IsOwnedByAnotherTorrent(first, hash))
        {
            usedPaths.Add(first);
            return first;
        }

        var shortHash = hash[..8];
        var suffixes = sameSyncCollision
            ? new[] { $" [{fileId}]", $" [{shortHash}-{fileId}]" }
            : new[] { $" [{shortHash}]", $" [{shortHash}-{fileId}]" };

        string? chosen = null;
        foreach (var suffix in suffixes)
        {
            var candidate = Path.Combine(dir, InsertBeforeExtension(strmFileName, suffix));
            if (!usedPaths.Contains(candidate) && !IsOwnedByAnotherTorrent(candidate, hash))
            {
                chosen = candidate;
                break;
            }
        }

        // Every candidate is taken: reuse the last one (this torrent's own file, rewritten in place).
        chosen ??= Path.Combine(dir, InsertBeforeExtension(strmFileName, suffixes[^1]));

        if (sameSyncCollision)
        {
            _logger.LogWarning(
                "Two video files of torrent {Hash} map to the same .strm name '{Name}'; writing '{Chosen}' for file {FileId}",
                hash, strmFileName, Path.GetFileName(chosen), fileId);
        }
        else
        {
            _logger.LogWarning(
                "'{Name}' already belongs to another torrent in {Dir}; writing '{Chosen}' for torrent {Hash}",
                strmFileName, dir, Path.GetFileName(chosen), hash);
        }

        usedPaths.Add(chosen);
        return chosen;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Deleting: cleanup, orphans, per-torrent and per-stream deletes. Every delete is limited to .strm files whose
    // parsed hash matches, their sidecar .nfo, and directories strictly inside a category root.
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Removes the .strm files (and sidecar .nfo) of <paramref name="torrentHash"/> that are not in
    /// <paramref name="activeStrmFiles"/>. Never touches a registered alternate stream or any other torrent's files.
    /// </summary>
    public async Task CleanupObsoleteFilesForTorrentAsync(
        string torrentHash,
        List<string> activeStrmFiles,
        CancellationToken cancellationToken = default)
    {
        var hash = TorrentHash.Normalize(torrentHash ?? string.Empty);
        if (!TorrentHash.IsValid(hash))
        {
            _logger.LogWarning("Refusing to clean up files for an invalid torrent hash: '{Hash}'", Truncate(torrentHash, 64));
            return;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var roots = GetCategoryRoots();
            var index = BuildStrmIndexCore(roots);
            CleanupObsoleteCore(hash, activeStrmFiles ?? new List<string>(), roots, index, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while cleaning up obsolete files for torrent {Hash}", hash);
        }
        finally
        {
            _lock.Release();
        }
    }

    private int CleanupObsoleteCore(
        string hash,
        IReadOnlyCollection<string> activePaths,
        IReadOnlyList<CategoryRoot> roots,
        Dictionary<string, List<string>> index,
        CancellationToken ct)
    {
        try
        {
            // Nothing written means nothing to compare against: deleting now would remove the torrent's last .strm.
            if (!TorrentHash.IsValid(hash) || activePaths.Count == 0)
            {
                return 0;
            }

            if (_altStore.TryGet(hash, out var alternate) && Directory.Exists(alternate.TargetDir))
            {
                return 0;
            }

            if (!index.TryGetValue(hash, out var candidates))
            {
                return 0;
            }

            var active = new HashSet<string>(
                activePaths.Select(p => Canonicalize(p) ?? p),
                PathComparer);

            var deleted = new List<string>();
            var affectedDirs = new HashSet<string>(PathComparer);

            foreach (var file in candidates.ToList())
            {
                ct.ThrowIfCancellationRequested();

                var canonical = Canonicalize(file) ?? file;
                if (active.Contains(canonical) || !IsInsideAnyRoot(roots, canonical) || !FileOwnedBy(file, hash))
                {
                    continue;
                }

                _logger.LogInformation("Deleting obsolete .strm duplicate for torrent {Hash}: {File}", hash, file);
                if (DeleteStrmWithSidecar(file))
                {
                    deleted.Add(file);
                    var parentDir = Path.GetDirectoryName(file);
                    if (!string.IsNullOrWhiteSpace(parentDir))
                    {
                        affectedDirs.Add(parentDir);
                    }
                }
            }

            if (deleted.Count == 0)
            {
                return 0;
            }

            candidates.RemoveAll(p => deleted.Contains(p, PathComparer));
            PruneAndNotify(affectedDirs, roots);
            return deleted.Count;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while cleaning up obsolete files for torrent {Hash}", hash);
            return 0;
        }
    }

    private int PurgeOrphans(
        Dictionary<string, List<string>> index,
        HashSet<string> activeHashes,
        IReadOnlyList<CategoryRoot> roots,
        CancellationToken ct)
    {
        var deleted = 0;
        var affectedDirs = new HashSet<string>(PathComparer);
        var now = DateTime.UtcNow;

        foreach (var (hash, paths) in index.ToList())
        {
            if (activeHashes.Contains(hash) || !TorrentHash.IsValid(hash))
            {
                continue;
            }

            foreach (var file in paths.ToList())
            {
                ct.ThrowIfCancellationRequested();

                if (!IsInsideAnyRoot(roots, file) || !FileOwnedBy(file, hash))
                {
                    continue;
                }

                try
                {
                    if (now - File.GetLastWriteTimeUtc(file) < OrphanGracePeriod)
                    {
                        _logger.LogDebug("Not purging recently written .strm {File} yet", file);
                        continue;
                    }
                }
                catch (Exception)
                {
                    continue;
                }

                if (DeleteStrmWithSidecar(file))
                {
                    deleted++;
                    _logger.LogInformation("Purged orphaned .strm file for deleted torrent {Hash}: {File}", hash, file);
                    var parentDir = Path.GetDirectoryName(file);
                    if (!string.IsNullOrWhiteSpace(parentDir))
                    {
                        affectedDirs.Add(parentDir);
                    }
                }
            }
        }

        if (deleted > 0)
        {
            PruneAndNotify(affectedDirs, roots);
        }

        return deleted;
    }

    /// <summary>
    /// Deletes every .strm whose parsed hash equals <paramref name="hash"/> in all category roots and Jellyfin
    /// library roots (an explicit, hash-targeted action, so files outside the category folders go too), plus its
    /// sidecar .nfo (never movie.nfo / tvshow.nfo / season.nfo), prunes the directories this empties (never the
    /// containing root itself or anything above it), and forgets the alternate-stream entry. Returns the number of
    /// .strm files deleted; an invalid hash deletes nothing.
    /// </summary>
    public async Task<int> DeleteTorrentFilesAsync(string hash, CancellationToken ct = default)
    {
        var normalized = TorrentHash.Normalize(hash ?? string.Empty);
        if (!TorrentHash.IsValid(normalized))
        {
            _logger.LogWarning("Refusing to delete files for an invalid torrent hash: '{Hash}'", Truncate(hash, 64));
            return 0;
        }

        await _lock.WaitAsync(ct);
        try
        {
            var roots = GetManagedRoots();
            var index = BuildStrmIndexCore(roots);
            var deleted = 0;
            var affectedDirs = new HashSet<string>(PathComparer);

            if (index.TryGetValue(normalized, out var paths))
            {
                foreach (var file in paths.ToList())
                {
                    ct.ThrowIfCancellationRequested();

                    if (!IsInsideAnyRoot(roots, file) || !FileOwnedBy(file, normalized))
                    {
                        continue;
                    }

                    if (DeleteStrmWithSidecar(file))
                    {
                        deleted++;
                        _logger.LogInformation("Deleted .strm {File} of torrent {Hash}", file, normalized);
                        var parentDir = Path.GetDirectoryName(file);
                        if (!string.IsNullOrWhiteSpace(parentDir))
                        {
                            affectedDirs.Add(parentDir);
                        }
                    }
                }
            }

            _altStore.Unregister(normalized);

            if (deleted > 0)
            {
                PruneAndNotify(affectedDirs, roots);
            }

            return deleted;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Deletes the .strm file and its same-named sidecar .nfo. The caller has already verified ownership.</summary>
    private bool DeleteStrmWithSidecar(string strmFile)
    {
        try
        {
            File.Delete(strmFile);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete .strm file {File}", strmFile);
            return false;
        }

        try
        {
            var dir = Path.GetDirectoryName(strmFile);
            if (!string.IsNullOrEmpty(dir))
            {
                var nfoName = Path.GetFileNameWithoutExtension(strmFile) + ".nfo";
                var nfoPath = Path.Combine(dir, nfoName);
                if (!SharedNfoNames.Contains(nfoName) && File.Exists(nfoPath))
                {
                    File.Delete(nfoPath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete the sidecar .nfo of {File}", strmFile);
        }

        return true;
    }

    private void PruneAndNotify(IEnumerable<string> directories, IReadOnlyList<CategoryRoot> roots)
    {
        var dirs = directories.ToList();
        foreach (var dir in dirs)
        {
            PruneEmptyDirectories(dir, roots);
        }

        var notified = new HashSet<string>(PathComparer);
        foreach (var dir in dirs)
        {
            var target = NearestExistingDirectory(dir);
            if (target != null && notified.Add(target))
            {
                NotifyPathChanged(target);
            }
        }
    }

    private static string? NearestExistingDirectory(string dir)
    {
        var current = dir;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (Directory.Exists(current))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    /// <summary>
    /// Walks upward from <paramref name="startDir"/> and deletes directories that hold nothing but disposable
    /// metadata. Stops at, and never deletes, the root (category or library root, whichever the caller passed)
    /// containing <paramref name="startDir"/>; outside every given root it does nothing. Any file that is not
    /// disposable (video, audio, subtitles, .strm, anything unknown) keeps its directory and ends the walk.
    /// </summary>
    private bool PruneEmptyDirectories(string startDir, IReadOnlyList<CategoryRoot>? roots = null)
    {
        roots ??= GetCategoryRoots();

        var current = Canonicalize(startDir);
        if (current == null)
        {
            return false;
        }

        var root = FindContainingRoot(roots, current);
        if (root == null)
        {
            return false;
        }

        var deletedAny = false;
        for (var level = 0; level < MaxPruneLevels; level++)
        {
            // Strictly inside the root: the root itself is never a candidate.
            if (current == null || PathEquals(current, root.Dir) || !IsInsideOrEqual(root.Dir, current))
            {
                break;
            }

            var candidate = current;
            if (Directory.Exists(candidate))
            {
                // Never delete a directory that is, or contains, a root.
                if (roots.Any(r => IsInsideOrEqual(candidate, r.Dir)))
                {
                    break;
                }

                if (!IsDisposableDirectory(candidate, 0) || !TryDeleteDisposableTree(candidate))
                {
                    break;
                }

                deletedAny = true;
                _logger.LogInformation("Deleted empty directory {Dir}", candidate);
            }

            current = Path.GetDirectoryName(candidate);
        }

        return deletedAny;
    }

    private static bool IsDisposableDirectory(string path, int depth)
    {
        if (depth > MaxDisposableDepth)
        {
            return false;
        }

        try
        {
            var directory = new DirectoryInfo(path);
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }

                if (entry is DirectoryInfo sub)
                {
                    if (!IsDisposableDirectory(sub.FullName, depth + 1))
                    {
                        return false;
                    }
                }
                else if (!DisposableExtensions.Contains(entry.Extension))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool TryDeleteDisposableTree(string dir)
    {
        try
        {
            DeleteDisposableContents(dir);
            Directory.Delete(dir, recursive: false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not delete directory {Dir}", dir);
            return false;
        }
    }

    private static void DeleteDisposableContents(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir).ToList())
        {
            // A file that appeared after the check and is not disposable stops the delete.
            if (!DisposableExtensions.Contains(Path.GetExtension(file)))
            {
                throw new IOException("Directory contains a file that is not disposable: " + file);
            }

            File.Delete(file);
        }

        foreach (var sub in Directory.EnumerateDirectories(dir).ToList())
        {
            DeleteDisposableContents(sub);
            Directory.Delete(sub, recursive: false);
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Alternate streams
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Alternates created by older versions are named "{folder} - {label}.strm" next to the item's primary stream.
    /// Registers their hash (if not registered yet) so syncing leaves them in place. A .strm only counts when its
    /// folder also holds a .strm of a different torrent (the primary version).
    /// </summary>
    private int MigrateLegacyAlternates(Dictionary<string, List<string>> index)
    {
        var hashesByDir = new Dictionary<string, HashSet<string>>(PathComparer);
        foreach (var (hash, paths) in index)
        {
            foreach (var path in paths)
            {
                var dir = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir))
                {
                    continue;
                }

                if (!hashesByDir.TryGetValue(dir, out var set))
                {
                    hashesByDir[dir] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                set.Add(hash);
            }
        }

        var registered = 0;
        foreach (var (hash, paths) in index)
        {
            if (_altStore.Contains(hash))
            {
                continue;
            }

            foreach (var path in paths)
            {
                var dir = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir))
                {
                    continue;
                }

                var parentName = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(parentName))
                {
                    continue;
                }

                var baseName = Path.GetFileNameWithoutExtension(path);
                if (!baseName.StartsWith(parentName + " - ", StringComparison.Ordinal) ||
                    !hashesByDir.TryGetValue(dir, out var hashesInDir) || hashesInDir.Count < 2)
                {
                    continue;
                }

                if (_altStore.RegisterIfMissing(hash, dir, null, path))
                {
                    registered++;
                }

                break;
            }
        }

        return registered;
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


    public string? ResolveItemDirectory(Guid itemId)
    {
        try
        {
            var item = _libraryManager.GetItemById(itemId);
            if (item == null) return null;

            if (!string.IsNullOrWhiteSpace(item.Path) && File.Exists(item.Path))
            {
                return Path.GetDirectoryName(item.Path);
            }
            if (!string.IsNullOrWhiteSpace(item.Path) && Directory.Exists(item.Path))
            {
                return item.Path;
            }
            if (!string.IsNullOrWhiteSpace(item.ContainingFolderPath) && Directory.Exists(item.ContainingFolderPath))
            {
                return item.ContainingFolderPath;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve directory for item {Id}", itemId);
        }
        return null;
    }

    public List<MovieStreamInfoDto> GetStreamsForDirectory(string targetDir, string? activeItemPath = null)
    {
        var result = new List<MovieStreamInfoDto>();
        try
        {
            if (!Directory.Exists(targetDir)) return result;

            var strmFiles = Directory.GetFiles(targetDir, "*.strm", SearchOption.TopDirectoryOnly);
            foreach (var strmFile in strmFiles)
            {
                var fileName = Path.GetFileName(strmFile);
                var baseName = Path.GetFileNameWithoutExtension(strmFile);
                var text = TryReadStrmText(strmFile);
                if (IsPlaceholderText(text))
                {
                    // A movie placeholder is not a version of the movie.
                    continue;
                }

                var hash = text != null && TorrentHash.TryExtractFromStrm(text, out var parsedHash) ? parsedHash : null;

                var isPrimary = (!string.IsNullOrWhiteSpace(activeItemPath) && strmFile.Equals(activeItemPath, StringComparison.OrdinalIgnoreCase))
                    || !baseName.Contains(" - ", StringComparison.Ordinal);

                var versionLabel = isPrimary ? "Основна версія" : "Версія";
                var dashIdx = baseName.IndexOf(" - ", StringComparison.Ordinal);
                if (dashIdx >= 0 && dashIdx + 3 < baseName.Length)
                {
                    versionLabel = baseName.Substring(dashIdx + 3).Trim();
                }
                else
                {
                    var bracketMatch = Regex.Match(baseName, @"\[([^\]]+)\]");
                    if (bracketMatch.Success)
                    {
                        versionLabel = bracketMatch.Groups[1].Value;
                    }
                }

                result.Add(new MovieStreamInfoDto
                {
                    Name = fileName,
                    Path = strmFile,
                    TorrentHash = hash,
                    VersionLabel = versionLabel,
                    IsPrimary = isPrimary
                });
            }

            return result.OrderByDescending(s => s.IsPrimary).ThenBy(s => s.Name, StringComparer.Ordinal).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read streams for directory {Dir}", targetDir);
            return result;
        }
    }

    /// <summary>
    /// Deletes the .strm files of torrent <paramref name="hash"/> that sit directly in <paramref name="targetDir"/>
    /// (matched by the parsed hash, not by substring), plus their sidecar .nfo. The directory must lie inside a
    /// category root or a Jellyfin library root. Returns true when at least one file was deleted.
    /// </summary>
    public async Task<bool> DeleteStreamByHashAsync(string targetDir, string hash, CancellationToken cancellationToken = default)
    {
        var normalized = TorrentHash.Normalize(hash ?? string.Empty);
        if (!TorrentHash.IsValid(normalized))
        {
            _logger.LogWarning("Refusing to delete a stream for an invalid torrent hash: '{Hash}'", Truncate(hash, 64));
            return false;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            return DeleteStreamByHashCore(targetDir, normalized, GetManagedRoots(), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete stream by hash {Hash} in {Dir}", normalized, targetDir);
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    // Callers hold the engine lock and pass a valid, normalised hash and the managed roots (category + library roots).
    private bool DeleteStreamByHashCore(string targetDir, string hash, IReadOnlyList<CategoryRoot> roots, CancellationToken ct)
    {
        var dir = Canonicalize(targetDir);
        if (dir == null || !Directory.Exists(dir))
        {
            return false;
        }

        if (FindContainingRoot(roots, dir) == null)
        {
            _logger.LogWarning("Refusing to delete streams in {Dir}: it is not inside a configured category folder or a Jellyfin library", dir);
            return false;
        }

        var deletedAny = false;
        foreach (var strmFile in Directory.EnumerateFiles(dir, "*.strm", StrmTopLevelEnumeration).ToList())
        {
            ct.ThrowIfCancellationRequested();

            if (!FileOwnedBy(strmFile, hash))
            {
                continue;
            }

            if (DeleteStrmWithSidecar(strmFile))
            {
                deletedAny = true;
                _logger.LogInformation("Deleted stream {File} for hash {Hash}", strmFile, hash);
            }
        }

        // A series torrent keeps its episodes in the season folders below its series folder; this top-folder delete
        // says nothing about them, so its registration stays (DeleteTorrentFilesAsync removes it with the episodes).
        var isSeriesTorrent = _altStore.TryGet(hash, out var registeredEntry) && registeredEntry.IsSeries;

        if (deletedAny)
        {
            if (!isSeriesTorrent)
            {
                _altStore.Unregister(hash);
            }

            PruneAndNotify(new[] { dir }, roots);
        }
        else if (!isSeriesTorrent && _altStore.TryGet(hash, out var entry) && PathEquals(Canonicalize(entry.TargetDir) ?? entry.TargetDir, dir))
        {
            // The alternate's file is already gone; drop the stale registration.
            _altStore.Unregister(hash);
        }

        return deletedAny;
    }

    private bool IsCategoryOrLibraryRoot(string canonicalDir, IReadOnlyList<CategoryRoot> roots)
    {
        if (roots.Any(r => PathEquals(r.Dir, canonicalDir)))
        {
            return true;
        }

        // Deliberately the unfiltered locations: an item folder must never be any library location, even one the
        // safety filter of GetLibraryRoots would not accept as a deletion root.
        foreach (var location in GetLibraryLocations())
        {
            var canonical = Canonicalize(location);
            if (canonical != null && PathEquals(canonical, canonicalDir))
            {
                return true;
            }
        }

        return false;
    }

    public async Task<AlternateStreamResultDto> AddAlternateStreamAsync(
        string targetDir,
        TorrTorrent torrent,
        AddAlternateStreamRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(torrent);
        ArgumentNullException.ThrowIfNull(request);

        var hash = TorrentHash.Normalize(torrent.Hash ?? string.Empty);
        var result = new AlternateStreamResultDto
        {
            TorrentHash = hash
        };

        if (!TorrentHash.IsValid(hash))
        {
            _logger.LogWarning("Refusing to add an alternate stream for an invalid torrent hash: '{Hash}'", Truncate(torrent.Hash, 64));
            result.ErrorMessage = "invalid torrent hash";
            return result;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var dir = Canonicalize(targetDir);
            if (dir == null || !Directory.Exists(dir))
            {
                result.ErrorMessage = "The target directory does not exist.";
                return result;
            }

            var roots = GetCategoryRoots();
            if (IsCategoryOrLibraryRoot(dir, roots))
            {
                // An item that lies loose in a library root has no folder of its own to hold alternate versions.
                result.ErrorMessage = "The target is a library root, not an item folder.";
                return result;
            }

            // A torrent added to a series keeps its episodes there; registering it as an alternate would drop that.
            if (_altStore.TryGet(hash, out var registered) && registered.IsSeries && Directory.Exists(registered.TargetDir))
            {
                result.ErrorMessage = "This torrent is already added to a series.";
                return result;
            }

            // Category roots only: this decides whether the folder is ours to (re)write metadata in. Library roots
            // are used further down solely to drop the replaced version.
            var insideCategoryRoots = FindContainingRoot(roots, dir) != null;

            var files = torrent.GetEffectiveFiles();
            var videoFiles = files.Where(f => EpisodeMatcher.IsVideoFile(f.Path)).ToList();
            if (videoFiles.Count == 0 && files.Count > 0)
            {
                videoFiles.Add(files[0]);
            }
            else if (videoFiles.Count == 0 && files.Count == 0)
            {
                videoFiles.Add(new TorrTorrentFile { Id = 1, Path = $"{Path.GetFileName(dir)}.mkv", Length = torrent.TorrentSize });
            }

            var mainFile = videoFiles.OrderByDescending(f => f.Length).FirstOrDefault() ?? videoFiles[0];

            var sizeBytes = mainFile.Length > 0 ? mainFile.Length : torrent.TorrentSize;
            var sizeFormatted = FormatFileSize(sizeBytes);

            var titleForQuality = !string.IsNullOrWhiteSpace(request.TorrentTitle)
                ? request.TorrentTitle
                : torrent.Title;

            var quality = !string.IsNullOrWhiteSpace(request.QualityTag)
                ? request.QualityTag
                : TitleCleaner.ExtractQualityTag(titleForQuality);

            var versionParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(quality))
            {
                versionParts.Add(quality);
            }
            if (!string.IsNullOrWhiteSpace(sizeFormatted))
            {
                versionParts.Add($"({sizeFormatted})");
            }

            var versionLabel = versionParts.Count > 0 ? string.Join(" ", versionParts) : "Alternate";

            // The version being replaced (only valid hashes ever reach the disk operations).
            string? oldHash = null;
            if (request.ReplaceExisting && !string.IsNullOrWhiteSpace(request.OldTorrentHash))
            {
                var candidate = TorrentHash.Normalize(request.OldTorrentHash);
                if (TorrentHash.IsValid(candidate) && !TorrentHash.Equal(candidate, hash))
                {
                    oldHash = candidate;
                }
                else
                {
                    _logger.LogWarning("Ignoring invalid or identical OldTorrentHash '{Hash}' while adding an alternate stream", Truncate(request.OldTorrentHash, 64));
                }
            }

            var baseFolderName = Path.GetFileName(dir);
            var strmFileName = BuildStrmFileName($"{baseFolderName} - {versionLabel}", baseFolderName, hash, wholeNameSanitized: true);
            var strmPath = Path.Combine(dir, strmFileName);

            // A movie that so far has only a placeholder: the new stream takes the placeholder's place as the primary
            // file (same file name, overwritten in place) instead of becoming a "{folder} - {label}" version next to it.
            // With a real stream already there, nothing changes (a stray placeholder is removed below).
            string? placeholderPath = null;
            var folderPlaceholders = FindPlaceholderStrms(dir);
            if (folderPlaceholders.Count > 0 && !HasNonPlaceholderStrm(dir))
            {
                placeholderPath = folderPlaceholders.FirstOrDefault(p =>
                    string.Equals(Path.GetFileNameWithoutExtension(p), baseFolderName, PathComparison)) ?? folderPlaceholders[0];
                strmPath = placeholderPath;
            }

            if (placeholderPath == null && File.Exists(strmPath))
            {
                // Reuse the file when it already belongs to this torrent or to the version being replaced;
                // otherwise keep both versions apart.
                var existingOwner = TryReadStrmHash(strmPath);
                var reusable = existingOwner != null &&
                    (TorrentHash.Equal(existingOwner, hash) || (oldHash != null && TorrentHash.Equal(existingOwner, oldHash)));
                if (!reusable)
                {
                    var withHash = BuildStrmFileName($"{baseFolderName} - {versionLabel} [{hash[..8]}]", baseFolderName, hash, wholeNameSanitized: true);
                    strmPath = Path.Combine(dir, withHash);
                }
            }

            var streamUrl = $"{_daemonManager.GetStreamBaseUrl()}/stream?link={hash}&index={mainFile.Id}&play\n";
            var changed = await WriteTextIfChangedAsync(strmPath, streamUrl, cancellationToken);

            // Ensure movie.nfo exists and locks localized Ukrainian title so Jellyfin preserves Ukrainian metadata.
            // Outside the managed category roots the folder belongs to somebody else: existing NFOs are left alone.
            var itemTitle = !string.IsNullOrWhiteSpace(request.LocalizedTitle) ? request.LocalizedTitle : baseFolderName;
            var (cleanTitle, origTitle, year) = TitleCleaner.CleanTitleAndExtractYear(itemTitle);
            var titleToUse = !string.IsNullOrWhiteSpace(request.LocalizedTitle) ? request.LocalizedTitle : cleanTitle;
            var origToUse = !string.IsNullOrWhiteSpace(request.OriginalTitle) ? request.OriginalTitle : origTitle;
            var yearToUse = request.Year ?? year;

            changed |= await WriteMovieNfoAsync(dir, baseFolderName, titleToUse, origToUse, yearToUse, request.TmdbId, request.ImdbId, createOnly: !insideCategoryRoots, cancellationToken);

            // Remember it, so syncing never turns this alternate into a standalone item.
            _altStore.Register(hash, dir, request.TargetItemId, strmPath);

            // No placeholder is left next to a real stream (only inside the managed roots, like every other delete).
            if (folderPlaceholders.Count > 0 && FindContainingRoot(GetManagedRoots(roots), dir) != null)
            {
                changed |= DeletePlaceholderStrms(dir, new[] { strmPath });
            }

            // Drop the replaced version only after the new one is safely on disk.
            if (oldHash != null)
            {
                changed |= DeleteStreamByHashCore(dir, oldHash, GetManagedRoots(roots), cancellationToken);
            }

            if (changed)
            {
                NotifyPathChanged(dir);
            }

            result.Success = true;
            result.StrmFilePath = strmPath;
            result.VersionLabel = versionLabel;
            _logger.LogInformation("Added alternate stream version '{Version}' for '{Folder}' in {Path}", versionLabel, baseFolderName, strmPath);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add alternate stream for torrent {Hash}", hash);
            result.Success = false;
            result.ErrorMessage = "Failed to add the alternate stream; see the Jellyfin log for details.";
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Movie placeholders: a library item (poster / metadata from its IMDb id) without a real stream yet. The movie's
    // folder gets "{name}.strm" (one line: {streamBase}/t2j-placeholder?imdb={ttid}) and movie.nfo; a real stream added
    // later takes the placeholder's place (AddAlternateStreamAsync, SyncMovieAsync). A placeholder carries no
    // link=hash, so none of the hash-based routines lists, syncs, migrates or deletes it.
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Creates the placeholder files of one movie in <paramref name="categoryName"/> (see
    /// <see cref="CreateMoviePlaceholdersAsync"/>, which this wraps).
    /// </summary>
    public async Task<PlaceholderResult> CreateMoviePlaceholderAsync(
        string categoryName,
        string title,
        int? year,
        string imdbId,
        int? tmdbId,
        CancellationToken ct = default)
    {
        var results = await CreateMoviePlaceholdersAsync(
            new[] { new PlaceholderRequest(categoryName, title, year, imdbId, tmdbId) }, ct);
        return results[0];
    }

    /// <summary>
    /// Creates the placeholder files for each movie, in order, under one hold of the engine lock (and with one scan of
    /// the category folders for existing placeholders). One result per request; a failing request never stops the
    /// others. Idempotent and non-destructive: a movie folder that already holds a .strm or video file, or a
    /// placeholder of the same IMDb id anywhere in the category folders, yields <see cref="PlaceholderResult.StatusExists"/>
    /// and nothing is written; nothing is ever overwritten or deleted. The folder and file names come from the same
    /// templates and sanitising as a real movie stream, so a stream added later lands in the same folder.
    /// </summary>
    public async Task<IReadOnlyList<PlaceholderResult>> CreateMoviePlaceholdersAsync(
        IReadOnlyList<PlaceholderRequest> items,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        var results = new List<PlaceholderResult>(items.Count);
        if (items.Count == 0)
        {
            return results;
        }

        await _lock.WaitAsync(ct);
        try
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var categories = GetEffectiveCategories(config);
            var roots = GetCategoryRoots();
            Dictionary<string, string>? known = null;

            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    results.Add(await CreateMoviePlaceholderCoreAsync(
                        item, config, categories, roots, () => known ??= BuildPlaceholderIndex(roots), ct));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to create a movie placeholder");
                    results.Add(new PlaceholderResult { Status = PlaceholderResult.StatusError, Reason = PlaceholderResult.ReasonWriteFailed });
                }
            }
        }
        finally
        {
            _lock.Release();
        }

        return results;
    }

    private static PlaceholderResult PlaceholderError(string reason) =>
        new() { Status = PlaceholderResult.StatusError, Reason = reason };

    // Maps the IMDb id of every placeholder .strm in the category folders to the folder it sits in (first one wins).
    private Dictionary<string, string> BuildPlaceholderIndex(IReadOnlyList<CategoryRoot> roots)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in GetScanRoots(roots))
        {
            try
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(root, "*.strm", StrmTreeEnumeration))
                {
                    var text = TryReadStrmText(file);
                    if (!IsPlaceholderText(text))
                    {
                        continue;
                    }

                    var match = PlaceholderImdbRegex.Match(text!);
                    var dir = Path.GetDirectoryName(file);
                    if (match.Success && !string.IsNullOrEmpty(dir))
                    {
                        index.TryAdd(match.Groups[1].Value, dir);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not enumerate .strm files in {Root}", root);
            }
        }

        return index;
    }

    // True when the folder (or anything below it) holds a .strm or a video file.
    private static bool HoldsStreamOrVideo(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", StrmTreeEnumeration)
                .Any(f => string.Equals(Path.GetExtension(f), ".strm", StringComparison.OrdinalIgnoreCase) || EpisodeMatcher.IsVideoFile(f));
        }
        catch (Exception)
        {
            // Unreadable: do not write into it.
            return true;
        }
    }

    // Callers hold the engine lock.
    private async Task<PlaceholderResult> CreateMoviePlaceholderCoreAsync(
        PlaceholderRequest? request,
        PluginConfiguration config,
        IReadOnlyList<CategoryMapping> categories,
        IReadOnlyList<CategoryRoot> roots,
        Func<Dictionary<string, string>> getKnownPlaceholders,
        CancellationToken ct)
    {
        var imdb = request?.ImdbId?.Trim() ?? string.Empty;
        if (request == null || !PlaceholderRequestImdbRegex.IsMatch(imdb))
        {
            return PlaceholderError(PlaceholderResult.ReasonInvalidImdb);
        }

        var title = (request.Title ?? string.Empty).Trim();
        if (title.Length == 0 || title.Length > MaxPlaceholderTitleLength)
        {
            return PlaceholderError(PlaceholderResult.ReasonInvalidTitle);
        }

        var year = request.Year;
        if (year.HasValue && (year.Value < MinPlaceholderYear || year.Value > MaxPlaceholderYear))
        {
            return PlaceholderError(PlaceholderResult.ReasonInvalidYear);
        }

        var category = categories.FirstOrDefault(c => c.Name.Equals(request.Category?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (category == null)
        {
            return PlaceholderError(PlaceholderResult.ReasonUnknownCategory);
        }

        if (!string.Equals(category.Type, "movie", StringComparison.OrdinalIgnoreCase))
        {
            return PlaceholderError(PlaceholderResult.ReasonNotMovieCategory);
        }

        // The category folder must be one of the (safe) managed roots; otherwise nothing is written.
        if (!TryResolveCategoryRoot(category, out var categoryDir) || !roots.Any(r => PathEquals(r.Dir, categoryDir)))
        {
            _logger.LogWarning("Category '{Category}' has no permitted root folder; not creating a placeholder for {Imdb}", category.Name, imdb);
            return PlaceholderError(PlaceholderResult.ReasonCategoryUnavailable);
        }

        // Same names as a real movie stream of this title / year.
        var rawTitle = TitleCleaner.FormatFolderTitle(title, year);
        var fileTitle = TitleCleaner.GetEnglishOrLatinTitle(title, null);
        if (TitleCleaner.SanitizeFilenameOrEmpty(fileTitle).Length == 0)
        {
            return PlaceholderError(PlaceholderResult.ReasonInvalidTitle);
        }

        var tokens = BuildMovieTokens(fileTitle, null, year, rawTitle);
        var folderName = ResolveMovieFolderName(config, tokens, rawTitle, fileTitle, year, imdb);
        var strmName = BuildSingleMovieStrmName(config, tokens, rawTitle, null, folderName, imdb);

        var movieDir = Path.Combine(categoryDir, folderName);
        var canonicalMovieDir = Canonicalize(movieDir);
        if (canonicalMovieDir == null || PathEquals(canonicalMovieDir, categoryDir) || !IsInsideOrEqual(categoryDir, canonicalMovieDir))
        {
            return PlaceholderError(PlaceholderResult.ReasonCategoryUnavailable);
        }

        var folderExisted = Directory.Exists(canonicalMovieDir);
        if (folderExisted && HoldsStreamOrVideo(canonicalMovieDir))
        {
            return new PlaceholderResult { Status = PlaceholderResult.StatusExists, Folder = canonicalMovieDir, Reason = PlaceholderResult.ReasonFolderHasStream };
        }

        var known = getKnownPlaceholders();
        if (known.TryGetValue(imdb, out var knownFolder) && Directory.Exists(knownFolder))
        {
            return new PlaceholderResult { Status = PlaceholderResult.StatusExists, Folder = knownFolder, Reason = PlaceholderResult.ReasonPlaceholderExists };
        }

        var strmPath = Path.Combine(canonicalMovieDir, strmName);
        var strmExisted = File.Exists(strmPath);
        try
        {
            Directory.CreateDirectory(canonicalMovieDir);

            var url = $"{_daemonManager.GetStreamBaseUrl()}/{PlaceholderMarker}?imdb={imdb}\n";
            await WriteTextIfChangedAsync(strmPath, url, ct);

            // Jellyfin fetches poster and metadata from the ids; an NFO that already exists is never replaced.
            await WriteMovieNfoAsync(canonicalMovieDir, folderName, title, null, year, request.TmdbId, imdb, createOnly: true, ct);
        }
        catch (Exception ex)
        {
            // Take back what this call added; whatever was there before is left alone.
            try
            {
                if (!strmExisted && File.Exists(strmPath))
                {
                    File.Delete(strmPath);
                }

                if (!folderExisted && Directory.Exists(canonicalMovieDir) && !Directory.EnumerateFileSystemEntries(canonicalMovieDir).Any())
                {
                    Directory.Delete(canonicalMovieDir, recursive: false);
                }
            }
            catch (Exception cleanupEx)
            {
                _logger.LogDebug(cleanupEx, "Could not take back the placeholder files in {Dir}", canonicalMovieDir);
            }

            if (ex is OperationCanceledException && ct.IsCancellationRequested)
            {
                throw;
            }

            _logger.LogError(ex, "Failed to write the placeholder for {Imdb} in {Dir}", imdb, canonicalMovieDir);
            return PlaceholderError(PlaceholderResult.ReasonWriteFailed);
        }

        known[imdb] = canonicalMovieDir;
        NotifyPathChanged(canonicalMovieDir);
        _logger.LogInformation("Created a movie placeholder for {Imdb} in {Dir}", imdb, canonicalMovieDir);
        return new PlaceholderResult { Status = PlaceholderResult.StatusCreated, Folder = canonicalMovieDir };
    }

    // ------------------------------------------------------------------------------------------------------------
    // Series torrents: a release (e.g. a new season pack) whose episodes are written into the folder of an
    // EXISTING series, laid out by season, next to the seasons that are already there.
    // ------------------------------------------------------------------------------------------------------------

    private sealed class SeriesMaterialization
    {
        /// <summary>TorrServer has not loaded the file list yet: nothing was written.</summary>
        public bool Pending { get; set; }

        /// <summary>The torrent has files, but none of them is a video: nothing was written.</summary>
        public bool NoVideoFiles { get; set; }

        /// <summary>A .strm was created or changed, or an obsolete one removed.</summary>
        public bool Changed { get; set; }

        public int VideoFiles { get; set; }

        /// <summary>Every .strm of the torrent in place in the series folder after the run.</summary>
        public List<string> Written { get; } = new();
    }

    /// <summary>
    /// Checks that <paramref name="path"/> can hold a series torrent: an existing directory strictly inside a category
    /// root or Jellyfin library root. A root itself (or a directory that contains one) is refused: such a folder is
    /// not a series folder of its own, and it is never written to or pruned.
    /// </summary>
    private bool TryValidateSeriesDirectory(string? path, IReadOnlyList<CategoryRoot> managedRoots, out string dir, out string? error)
    {
        dir = string.Empty;
        error = null;

        var canonical = string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) ? null : Canonicalize(path);
        if (canonical == null || !Directory.Exists(canonical))
        {
            error = "The series folder does not exist.";
            return false;
        }

        if (IsCategoryOrLibraryRoot(canonical, managedRoots) || managedRoots.Any(r => IsInsideOrEqual(canonical, r.Dir)))
        {
            error = "The series folder is a category or library root, not a folder of a series.";
            return false;
        }

        if (FindContainingRoot(managedRoots, canonical) == null)
        {
            error = "The series folder is outside the configured category folders and Jellyfin libraries.";
            return false;
        }

        dir = canonical;
        return true;
    }

    /// <summary>
    /// True when <paramref name="path"/> is a folder a torrent may be added to as a series torrent (see
    /// <see cref="AddTorrentToSeriesAsync"/>). <paramref name="error"/> says why not.
    /// </summary>
    public bool IsValidSeriesDirectory(string? path, out string? error)
    {
        return TryValidateSeriesDirectory(path, GetManagedRoots(), out _, out error);
    }

    /// <summary>
    /// Writes the episodes of <paramref name="torrent"/> into the existing series folder <paramref name="seriesDir"/>
    /// and remembers the torrent as a series torrent, so that later syncs keep them there instead of creating a
    /// standalone folder. The episodes get exactly the parsing and naming of a normal show sync (season and episode
    /// numbers, season folder / episode file templates, collision and foreign-file rules, write-if-changed), with the
    /// series folder fixed to <paramref name="seriesDir"/> and the title taken from that folder's name.
    /// <c>tvshow.nfo</c> and posters of the series are never written or replaced, other torrents' files are never
    /// touched, and only this torrent's own obsolete .strm files inside <paramref name="seriesDir"/> are removed.
    /// While TorrServer has not loaded the file list yet the torrent is only registered
    /// (<see cref="SeriesTorrentResultDto.Pending"/>): the next sync writes the episodes.
    /// A failure is reported through <see cref="SeriesTorrentResultDto.Failure"/>; a run that fails halfway takes back
    /// what it created.
    /// </summary>
    public async Task<SeriesTorrentResultDto> AddTorrentToSeriesAsync(
        string seriesDir,
        TorrTorrent torrent,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(torrent);

        var hash = TorrentHash.Normalize(torrent.Hash ?? string.Empty);
        var result = new SeriesTorrentResultDto { TorrentHash = hash };

        if (!TorrentHash.IsValid(hash))
        {
            _logger.LogWarning("Refusing to add a series torrent with an invalid hash: '{Hash}'", Truncate(torrent.Hash, 64));
            result.Failure = SeriesTorrentFailure.InvalidHash;
            result.Message = "invalid torrent hash";
            return result;
        }

        await _lock.WaitAsync(ct);
        try
        {
            return await AddTorrentToSeriesCoreAsync(seriesDir, torrent, hash, result, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    // Callers hold the engine lock and pass a valid, normalised hash.
    private async Task<SeriesTorrentResultDto> AddTorrentToSeriesCoreAsync(
        string seriesDir,
        TorrTorrent torrent,
        string hash,
        SeriesTorrentResultDto result,
        CancellationToken ct)
    {
        var tracker = new WriteTracker();
        var registeredByThisCall = false;

        try
        {
            var categoryRoots = GetCategoryRoots();
            var managedRoots = GetManagedRoots(categoryRoots);

            if (!TryValidateSeriesDirectory(seriesDir, managedRoots, out var dir, out var targetError))
            {
                result.Failure = SeriesTorrentFailure.InvalidTarget;
                result.Message = targetError;
                return result;
            }

            // A torrent lives in exactly one place. An entry whose folder is gone is stale and simply replaced.
            var registeredHere = false;
            if (_altStore.TryGet(hash, out var existing) && Directory.Exists(existing.TargetDir))
            {
                var existingDir = Canonicalize(existing.TargetDir) ?? existing.TargetDir;
                if (!existing.IsSeries || !PathEquals(existingDir, dir))
                {
                    _logger.LogWarning(
                        "Not adding torrent {Hash} to series folder {Dir}: it is already registered ({Kind}) for {Existing}",
                        hash, dir, existing.Kind, existing.TargetDir);
                    result.Failure = SeriesTorrentFailure.Conflict;
                    result.Message = existing.IsSeries
                        ? "This torrent is already added to another series."
                        : "This torrent is already added as an alternate version of another item.";
                    return result;
                }

                registeredHere = true;
            }

            if (!registeredHere)
            {
                // Same torrent already in the library as an item of its own (or as an alternate that is not registered
                // yet): adding it here too would show every episode twice. The user removes it first.
                var index = BuildStrmIndexCore(managedRoots);
                if (index.TryGetValue(hash, out var existingFiles) &&
                    existingFiles.Any(f => File.Exists(f) && !IsInsideOrEqual(dir, Canonicalize(f) ?? f)))
                {
                    _logger.LogWarning("Not adding torrent {Hash} to series folder {Dir}: it already has .strm files elsewhere in the library", hash, dir);
                    result.Failure = SeriesTorrentFailure.Conflict;
                    result.Message = "This torrent is already in the library as a separate item. Remove it first, then add it to the series.";
                    return result;
                }
            }

            var files = torrent.GetEffectiveFiles();
            if (files.Count > 0 && !files.Any(f => EpisodeMatcher.IsVideoFile(f.Path)))
            {
                result.Failure = SeriesTorrentFailure.NoVideoFiles;
                result.Message = "The torrent contains no video files.";
                return result;
            }

            if (!registeredHere)
            {
                _altStore.Register(hash, dir, null, string.Empty, AlternateStreamEntry.KindSeries);
                registeredByThisCall = true;
            }

            var outcome = await MaterializeSeriesAsync(dir, hash, torrent, managedRoots, tracker, ct);
            if (outcome.Pending)
            {
                _logger.LogInformation("Torrent {Hash} is registered for series folder {Dir}; its file list is not ready yet", hash, dir);
                result.Success = true;
                result.Pending = true;
                result.Message = "TorrServer has not loaded the torrent's file list yet; the episodes will appear after the next sync.";
                return result;
            }

            if (outcome.NoVideoFiles)
            {
                if (registeredByThisCall)
                {
                    _altStore.Unregister(hash);
                }

                result.Failure = SeriesTorrentFailure.NoVideoFiles;
                result.Message = "The torrent contains no video files.";
                return result;
            }

            result.Success = true;
            result.EpisodesWritten = outcome.Written.Count;
            _logger.LogInformation("Added {Count} episode(s) of torrent {Hash} to series folder {Dir}", outcome.Written.Count, hash, dir);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add torrent {Hash} to series folder {Dir}", hash, seriesDir);
            RollbackSeriesAdd(tracker, hash, registeredByThisCall);
            result.Success = false;
            result.Pending = false;
            result.EpisodesWritten = 0;
            result.Failure = SeriesTorrentFailure.WriteFailed;
            result.Message = "Failed to write the STRM files; see the Jellyfin log for details.";
            return result;
        }
    }

    // Takes back what a failed AddTorrentToSeries run created: its new files, the season folders it made (when still
    // empty) and its registration. Files and folders that were already there are left alone.
    private void RollbackSeriesAdd(WriteTracker tracker, string hash, bool registeredByThisCall)
    {
        foreach (var file in tracker.CreatedFiles)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not remove {File} while rolling back a failed series torrent add", file);
            }
        }

        foreach (var directory in Enumerable.Reverse(tracker.CreatedDirectories))
        {
            try
            {
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory, recursive: false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not remove {Dir} while rolling back a failed series torrent add", directory);
            }
        }

        if (registeredByThisCall)
        {
            _altStore.Unregister(hash);
        }
    }

    // Re-materialises a registered series torrent during a sync. Returns false when its folder can no longer be used,
    // so that the caller falls back to the standard path. A torrent whose file list is not ready yet comes back as
    // "metadata not ready" (counted as waiting, not as failed).
    private async Task<bool> TrySyncRegisteredSeriesAsync(
        TorrTorrent torrent,
        string hash,
        AlternateStreamEntry entry,
        IReadOnlyList<CategoryRoot> categoryRoots,
        StrmSyncResult result,
        CancellationToken ct)
    {
        var managedRoots = GetManagedRoots(categoryRoots);
        if (!TryValidateSeriesDirectory(entry.TargetDir, managedRoots, out var dir, out var reason))
        {
            WarnRootOnce(
                $"series|{hash}|{entry.TargetDir}",
                "The series folder '{Dir}' of torrent {Hash} can no longer be used ({Reason}); it is synced like an unregistered torrent",
                entry.TargetDir, hash, reason ?? string.Empty);
            return false;
        }

        var outcome = await MaterializeSeriesAsync(dir, hash, torrent, managedRoots, tracker: null, ct);

        result.FolderPath = dir;
        result.Category = GetCategoryNameForPath(dir, categoryRoots) ?? string.Empty;
        result.VideoFilesCount = outcome.VideoFiles;
        result.CreatedStrmFiles.AddRange(outcome.Written);

        if (outcome.Pending)
        {
            _logger.LogDebug("Metadata of series torrent {Hash} is not ready; nothing written to {Dir}", hash, dir);
            result.ErrorMessage = MetadataNotReadyMessage;
            return true;
        }

        result.Success = true;
        _logger.LogDebug("Series torrent {Hash} is up to date in {Dir} ({Count} episode(s))", hash, dir, outcome.Written.Count);
        return true;
    }

    // Writes the episodes of a series torrent into the (validated, canonical) series folder and removes the torrent's own
    // obsolete .strm files there. Nothing is written while the file list is missing. tvshow.nfo and posters are never touched.
    private async Task<SeriesMaterialization> MaterializeSeriesAsync(
        string seriesDir,
        string hash,
        TorrTorrent torrent,
        IReadOnlyList<CategoryRoot> managedRoots,
        WriteTracker? tracker,
        CancellationToken ct)
    {
        var outcome = new SeriesMaterialization();

        var files = torrent.GetEffectiveFiles();
        if (files.Count == 0)
        {
            outcome.Pending = true;
            return outcome;
        }

        var videoFiles = files.Where(f => EpisodeMatcher.IsVideoFile(f.Path)).ToList();
        outcome.VideoFiles = videoFiles.Count;
        if (videoFiles.Count == 0)
        {
            outcome.NoVideoFiles = true;
            return outcome;
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        // The series already exists under its own name: that name (without "(Year)") is the title of the episodes.
        var folderName = Path.GetFileName(seriesDir);
        var (cleanTitle, _, year) = TitleCleaner.CleanTitleAndExtractYear(folderName);
        var title = string.IsNullOrWhiteSpace(cleanTitle) ? folderName : cleanTitle;
        var showTokens = BuildShowTokens(title, null, year, folderName);

        outcome.Changed |= await WriteShowEpisodesAsync(config, hash, showTokens, seriesDir, videoFiles, outcome.Written, tracker, ct);

        // Only this torrent's own files, only inside the series folder, and only once something was written.
        if (outcome.Written.Count > 0 && DeleteObsoleteStrmInDirectory(seriesDir, hash, outcome.Written, managedRoots, ct) > 0)
        {
            outcome.Changed = true;
        }

        if (outcome.Changed)
        {
            NotifyPathChanged(seriesDir);
        }

        return outcome;
    }

    // Deletes the .strm files (with sidecar .nfo) of <hash> below <dir> that are not in <activePaths>: leftovers of an
    // earlier run with other names. Files of other torrents, and anything outside <dir>, are never touched.
    private int DeleteObsoleteStrmInDirectory(
        string dir,
        string hash,
        IReadOnlyCollection<string> activePaths,
        IReadOnlyList<CategoryRoot> roots,
        CancellationToken ct)
    {
        var active = new HashSet<string>(activePaths.Select(p => Canonicalize(p) ?? p), PathComparer);
        var deleted = 0;
        var affectedDirs = new HashSet<string>(PathComparer);

        foreach (var file in Directory.EnumerateFiles(dir, "*.strm", StrmTreeEnumeration).ToList())
        {
            ct.ThrowIfCancellationRequested();

            var canonical = Canonicalize(file) ?? file;
            if (active.Contains(canonical) || !IsInsideOrEqual(dir, canonical) || !FileOwnedBy(file, hash))
            {
                continue;
            }

            _logger.LogInformation("Deleting obsolete .strm of series torrent {Hash}: {File}", hash, file);
            if (DeleteStrmWithSidecar(file))
            {
                deleted++;
                var parentDir = Path.GetDirectoryName(file);
                if (!string.IsNullOrWhiteSpace(parentDir))
                {
                    affectedDirs.Add(parentDir);
                }
            }
        }

        if (deleted > 0)
        {
            PruneAndNotify(affectedDirs, roots);
        }

        return deleted;
    }

    // ------------------------------------------------------------------------------------------------------------
    // File writing
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Writes <paramref name="content"/> unless the file already holds exactly that text. Returns true when written.</summary>
    private static async Task<bool> WriteTextIfChangedAsync(string path, string content, CancellationToken ct)
    {
        if (File.Exists(path))
        {
            try
            {
                var existing = await File.ReadAllTextAsync(path, ct);
                if (string.Equals(existing, content, StringComparison.Ordinal))
                {
                    return false;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // unreadable: fall through and try to write
            }
        }

        await File.WriteAllTextAsync(path, content, Encoding.UTF8, ct);
        return true;
    }

    private static async Task<bool> WriteNfoAsync(string path, string content, bool preserveExisting, CancellationToken ct)
    {
        if (preserveExisting && File.Exists(path))
        {
            return false;
        }

        return await WriteTextIfChangedAsync(path, content, ct);
    }

    private async Task<bool> DownloadPosterAsync(string targetDir, string posterUrl, CancellationToken ct)
    {
        string? tmpPath = null;
        try
        {
            var posterPath = Path.Combine(targetDir, "poster.jpg");
            if (File.Exists(posterPath))
            {
                return false;
            }

            if (!Uri.TryCreate(posterUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                _logger.LogWarning("Ignoring poster URL that is not http(s)");
                return false;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Poster download from {Host} failed with HTTP {Status}", uri.Host, (int)response.StatusCode);
                return false;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType == null || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Ignoring poster from {Host}: content type '{Type}' is not an image", uri.Host, mediaType);
                return false;
            }

            if (response.Content.Headers.ContentLength is long declared && declared > MaxPosterBytes)
            {
                _logger.LogWarning("Ignoring poster from {Host}: {Size} bytes is over the {Max} byte limit", uri.Host, declared, MaxPosterBytes);
                return false;
            }

            await using var source = await response.Content.ReadAsStreamAsync(ct);
            tmpPath = Path.Combine(targetDir, $".poster-{Guid.NewGuid():N}.tmp");
            long total = 0;

            await using (var destination = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(), ct)) > 0)
                {
                    total += read;
                    if (total > MaxPosterBytes)
                    {
                        _logger.LogWarning("Ignoring poster from {Host}: it is over the {Max} byte limit", uri.Host, MaxPosterBytes);
                        return false;
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }

            if (total == 0)
            {
                return false;
            }

            File.Move(tmpPath, posterPath, overwrite: false);
            tmpPath = null;
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download poster");
            return false;
        }
        finally
        {
            if (tmpPath != null)
            {
                try
                {
                    File.Delete(tmpPath);
                }
                catch (Exception)
                {
                    // best effort
                }
            }
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // NFO
    // ------------------------------------------------------------------------------------------------------------

    private async Task<bool> WriteMovieNfoAsync(
        string movieDir,
        string folderName,
        string title,
        string? originalTitle,
        int? year,
        int? tmdbId,
        string? imdbId,
        bool createOnly,
        CancellationToken ct)
    {
        try
        {
            var tmdb = tmdbId.HasValue && tmdbId.Value > 0 ? tmdbId : null;
            var imdb = NormalizeImdbId(imdbId);

            // Without ids this call must not replace an NFO that may carry them (e.g. from the original add).
            var preserveExisting = createOnly || (!tmdb.HasValue && imdb == null);
            var nfoContent = BuildMovieNfoXml(title, originalTitle, year, tmdb, imdb);

            var changed = false;
            var movieNfoPath = Path.Combine(movieDir, "movie.nfo");
            changed |= await WriteNfoAsync(movieNfoPath, nfoContent, preserveExisting, ct);

            var namedNfoPath = Path.Combine(movieDir, $"{folderName}.nfo");
            if (!PathEquals(namedNfoPath, movieNfoPath))
            {
                changed |= await WriteNfoAsync(namedNfoPath, nfoContent, preserveExisting, ct);
            }

            return changed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write movie NFO for {Title}", title);
            return false;
        }
    }

    private async Task<bool> WriteMovieItemNfoAsync(
        string movieDir,
        string itemBaseName,
        string title,
        string? originalTitle,
        int? year,
        CancellationToken ct)
    {
        try
        {
            // Per-item NFOs of a collection carry no ids, so an existing one is never replaced.
            var nfoContent = BuildMovieNfoXml(title, originalTitle, year, null, null);
            var itemNfoPath = Path.Combine(movieDir, $"{itemBaseName}.nfo");
            return await WriteNfoAsync(itemNfoPath, nfoContent, preserveExisting: true, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write movie item NFO for {Title}", title);
            return false;
        }
    }

    private async Task<bool> WriteShowNfoAsync(
        string showDir,
        string title,
        string? originalTitle,
        int? year,
        int? tmdbId,
        string? imdbId,
        CancellationToken ct)
    {
        try
        {
            var tmdb = tmdbId.HasValue && tmdbId.Value > 0 ? tmdbId : null;
            var imdb = NormalizeImdbId(imdbId);
            var hasId = tmdb.HasValue || imdb != null;

            var sb = new StringBuilder();
            AppendLine(sb, "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\" ?>");
            AppendLine(sb, "<tvshow>");
            AppendTitleBlock(sb, title, originalTitle, year, hasId);
            if (tmdb.HasValue)
            {
                AppendLine(sb, $"  <uniqueid type=\"tmdb\" default=\"true\">{tmdb.Value.ToString(CultureInfo.InvariantCulture)}</uniqueid>");
                AppendLine(sb, $"  <tmdbid>{tmdb.Value.ToString(CultureInfo.InvariantCulture)}</tmdbid>");
            }
            if (imdb != null)
            {
                // Never a bare <id>: Jellyfin reads that as the TVDB id of a series.
                AppendLine(sb, $"  <uniqueid type=\"imdb\"{(tmdb.HasValue ? string.Empty : " default=\"true\"")}>{Xml(imdb)}</uniqueid>");
                AppendLine(sb, $"  <imdb_id>{Xml(imdb)}</imdb_id>");
            }
            AppendLine(sb, "</tvshow>");

            var nfoPath = Path.Combine(showDir, "tvshow.nfo");
            return await WriteNfoAsync(nfoPath, sb.ToString(), preserveExisting: !hasId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write show NFO for {Title}", title);
            return false;
        }
    }

    private static string BuildMovieNfoXml(string title, string? originalTitle, int? year, int? tmdbId, string? imdbId)
    {
        var hasId = tmdbId.HasValue || imdbId != null;
        var sb = new StringBuilder();
        AppendLine(sb, "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\" ?>");
        AppendLine(sb, "<movie>");
        AppendTitleBlock(sb, title, originalTitle, year, hasId);
        if (tmdbId.HasValue)
        {
            AppendLine(sb, $"  <uniqueid type=\"tmdb\" default=\"true\">{tmdbId.Value.ToString(CultureInfo.InvariantCulture)}</uniqueid>");
            AppendLine(sb, $"  <tmdbid>{tmdbId.Value.ToString(CultureInfo.InvariantCulture)}</tmdbid>");
        }
        if (imdbId != null)
        {
            AppendLine(sb, $"  <uniqueid type=\"imdb\"{(tmdbId.HasValue ? string.Empty : " default=\"true\"")}>{Xml(imdbId)}</uniqueid>");
            AppendLine(sb, $"  <imdbid>{Xml(imdbId)}</imdbid>");
            AppendLine(sb, $"  <id>{Xml(imdbId)}</id>");
        }
        AppendLine(sb, "</movie>");
        return sb.ToString();
    }

    /// <summary>
    /// Writes the title/originaltitle/year elements (each at most once).
    /// If external IDs exist, title/originaltitle are omitted so Jellyfin fetches metadata in the library's language.
    /// Without an external ID: a Cyrillic title is written as &lt;title&gt; (already localized); a Latin title is
    /// written only as &lt;originaltitle&gt;, so Jellyfin matches against TMDB and fills in the localized title.
    /// </summary>
    private static void AppendTitleBlock(StringBuilder sb, string title, string? originalTitle, int? year, bool hasId)
    {
        title ??= string.Empty;
        var hasCyrillic = title.Any(c => (c >= 0x0400 && c <= 0x04FF) || (c >= 0x0500 && c <= 0x052F));
        if (hasCyrillic)
        {
            AppendLine(sb, $"  <title>{Xml(title)}</title>");
        }

        string? originalToWrite = null;
        if (!string.IsNullOrWhiteSpace(originalTitle) && !originalTitle.Equals(title, StringComparison.OrdinalIgnoreCase))
        {
            originalToWrite = originalTitle;
        }
        else if (!hasCyrillic && !hasId && !string.IsNullOrWhiteSpace(title))
        {
            originalToWrite = title;
        }

        if (originalToWrite != null)
        {
            AppendLine(sb, $"  <originaltitle>{Xml(originalToWrite)}</originaltitle>");
        }

        if (year.HasValue)
        {
            AppendLine(sb, $"  <year>{year.Value.ToString(CultureInfo.InvariantCulture)}</year>");
        }
    }

    private static void AppendLine(StringBuilder sb, string line)
    {
        sb.Append(line).Append('\n');
    }

    /// <summary>Strips characters that are invalid in XML 1.0, then escapes the rest.</summary>
    private static string Xml(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                sb.Append(c).Append(value[i + 1]);
                i++;
            }
            else if (XmlConvert.IsXmlChar(c))
            {
                sb.Append(c);
            }
        }

        return SecurityElement.Escape(sb.ToString()) ?? string.Empty;
    }

    /// <summary>"tt0133093", "TT0133093" or a bare number become "tt0133093"; anything else is dropped (null).</summary>
    private static string? NormalizeImdbId(string? imdbId)
    {
        if (string.IsNullOrWhiteSpace(imdbId))
        {
            return null;
        }

        var id = imdbId.Trim();
        if (id.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
        {
            id = "tt" + id[2..];
        }
        else if (long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0)
        {
            id = "tt" + number.ToString("D7", CultureInfo.InvariantCulture);
        }

        return Regex.IsMatch(id, @"^tt\d{1,10}$") ? id : null;
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
