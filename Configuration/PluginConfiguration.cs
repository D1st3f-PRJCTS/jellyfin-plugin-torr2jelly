using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Torr2Jelly.Configuration;

public class TrackerRule
{
    public string Tracker { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public List<string> AllowedTags { get; set; } = new();

    public TrackerRule()
    {
    }

    public TrackerRule(string tracker, bool enabled, List<string> allowedTags)
    {
        Tracker = tracker;
        Enabled = enabled;
        AllowedTags = allowedTags;
    }
}

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Version of the configuration layout. <c>Plugin</c> runs a one-time migration for every stored
    /// configuration whose <see cref="ConfigVersion"/> is lower than this value.
    /// </summary>
    public const int CurrentConfigVersion = 2;

    // Configuration layout version (0 = written by a release that had no migrations). Managed by the plugin only.
    public int ConfigVersion { get; set; } = 0;

    // TorrServer Settings
    public string ExternalTorrServerUrl { get; set; } = "http://torrserver:8090";
    public string TorrServerStreamUrl { get; set; } = "http://torrserver:8090"; // URL written into .strm files for playback

    // Torrent Cache & Performance
    // These are pushed to TorrServer only when ManageTorrServerSettings is on.
    public bool ManageTorrServerSettings { get; set; } = false;
    public int CacheSizeMb { get; set; } = 102400; // 100 GB default
    public int PreloadCachePercent { get; set; } = 3; // 3% of CacheSize (~3 GB for 100GB cache, starts in 2s, provides ~150s buffer)
    public int ReaderReadAHeadPercent { get; set; } = 95; // 95% sliding read-ahead buffer (recommended)
    public int TorrentDisconnectTimeout { get; set; } = 86400; // 86400 seconds = 24 hours seeding duration after playback
    public bool UseDiskCache { get; set; } = true;
    public string DiskCachePath { get; set; } = string.Empty;

    // Optional file holding the peer port forwarded by a VPN (e.g. Proton). Several candidate files may be given,
    // separated by ';': the first one that exists and holds a valid port is used. When set and managing TorrServer
    // settings, the port is pushed to TorrServer as PeersListenPort.
    public string ForwardedPortFilePath { get; set; } = string.Empty;

    // Search Indexer (Prowlarr / Torznab)
    public bool EnableProwlarr { get; set; } = true;
    public string ProwlarrUrl { get; set; } = string.Empty;
    public string ProwlarrApiKey { get; set; } = string.Empty;

    public bool EnableTorznab { get; set; } = false;
    public string TorznabUrl { get; set; } = string.Empty;
    public string TorznabApiKey { get; set; } = string.Empty;

    // Permissions & Filters
    public bool SearchOnlyGoldByDefault { get; set; } = false;
    public bool OnlyGoldTorrentsForNonAdmins { get; set; } = false;
    public List<string> DisabledTrackers { get; set; } = new();
    public List<TrackerRule> TrackerRules { get; set; } = new();
    public string DefaultLanguage { get; set; } = "uk"; // "uk" (default) or "en"

    // Library & Storage
    public string MediaRootPath { get; set; } = "/media";
    public bool AutoSyncTorrents { get; set; } = true;
    public int AutoSyncIntervalMinutes { get; set; } = 15;

    // Renaming toggles
    public bool EnableMovieRenaming { get; set; } = true;
    public bool EnableShowRenaming { get; set; } = true;

    // STRM File & Folder Structure Templates
    public string MovieFolderTemplate { get; set; } = "{Title} ({Year})";
    public string MovieFileTemplate { get; set; } = "{Title} ({Year}).strm";
    public string ShowFolderTemplate { get; set; } = "{Title} ({Year})";
    public string ShowSeasonFolderTemplate { get; set; } = "Season {Season:00}";
    public string ShowEpisodeFileTemplate { get; set; } = "{Title} - {EpisodeTag}.strm";

    // Categories (initialized empty to prevent XmlSerializer from appending to defaults upon deserialization)
    public List<CategoryMapping> Categories { get; set; } = new();

    /// <summary>
    /// Default categories of a fresh installation. The folders are relative: they are resolved under
    /// <see cref="MediaRootPath"/> (or the Jellyfin library root when that folder does not exist).
    /// </summary>
    public static List<CategoryMapping> GetDefaultCategories() => new()
    {
        new CategoryMapping("Movies", "movies", "movie", new List<int> { 2000, 2010, 2020, 2030, 2040, 2045, 2050, 2060, 2070 }, true),
        new CategoryMapping("Shows", "shows", "show", new List<int> { 5000, 5010, 5020, 5030, 5040, 5045, 5050, 5060 }, true),
        new CategoryMapping("Cartoons", "cartoons", "movie", new List<int> { 2000, 5000 }, false),
        new CategoryMapping("Anime", "anime", "show", new List<int> { 5070 }, false)
    };

    /// <summary>
    /// The absolute default categories that releases up to 1.0.18 used when none were stored. Only the one-time
    /// migration of an existing installation uses them, so its .strm files keep resolving to the same folders.
    /// </summary>
    public static List<CategoryMapping> GetLegacyDefaultCategories() => new()
    {
        new CategoryMapping("Movies", "/mnt/sdb/media/movies", "movie", new List<int> { 2000, 2010, 2020, 2030, 2040, 2045, 2050, 2060, 2070 }, true),
        new CategoryMapping("Shows", "/mnt/sdb/media/shows", "show", new List<int> { 5000, 5010, 5020, 5030, 5040, 5045, 5050, 5060 }, true),
        new CategoryMapping("Cartoons", "/mnt/sdb/media/cartoons", "movie", new List<int> { 2000, 5000 }, false),
        new CategoryMapping("Anime", "/mnt/sdb/media/anime", "show", new List<int> { 5070 }, false)
    };

    public void DeduplicateCategories()
    {
        if (Categories == null || Categories.Count == 0)
        {
            Categories = GetDefaultCategories();
            return;
        }

        var unique = new List<CategoryMapping>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cat in Categories)
        {
            if (cat == null || string.IsNullOrWhiteSpace(cat.Name)) continue;
            if (seenNames.Add(cat.Name.Trim()))
            {
                unique.Add(cat);
            }
        }
        Categories = unique.Count > 0 ? unique : GetDefaultCategories();
    }

    public void NormalizeTrackerRules()
    {
        if (TrackerRules == null)
        {
            TrackerRules = new List<TrackerRule>();
            return;
        }

        var allKnown = new List<string> { "freeleech", "halfleech", "bronze", "internal", "exclusive", "doubleupload", "regular" };
        foreach (var rule in TrackerRules)
        {
            if (rule == null) continue;
            if (rule.AllowedTags == null || rule.AllowedTags.Count == 0)
            {
                rule.AllowedTags = new List<string>(allKnown);
            }
        }
    }

    public PluginConfiguration()
    {
    }
}
