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
    // Embedded Standalone TorrServer Daemon settings
    public bool UseEmbeddedTorrServer { get; set; } = false;
    public int EmbeddedTorrServerPort { get; set; } = 8095;
    public string ExternalTorrServerUrl { get; set; } = "http://torrserver:8090";
    public string TorrServerStreamUrl { get; set; } = "http://torrserver:8090"; // URL embedded into .strm files

    // Torrent Cache & Performance
    public int CacheSizeMb { get; set; } = 256;
    public int PreloadCachePercent { get; set; } = 50;
    public int ReaderReadAHeadPercent { get; set; } = 95;
    public bool UseDiskCache { get; set; } = false;
    public string DiskCachePath { get; set; } = string.Empty;

    // Search Indexer (Prowlarr / Torznab)
    public bool EnableProwlarr { get; set; } = true;
    public string ProwlarrUrl { get; set; } = "https://prowlarr.d1st3f.pp.ua";
    public string ProwlarrApiKey { get; set; } = "dffa6bebf5b34909936a3ae0b4e9c612";

    public bool EnableTorznab { get; set; } = false;
    public string TorznabUrl { get; set; } = string.Empty;
    public string TorznabApiKey { get; set; } = string.Empty;

    // Permissions & Filters
    public bool SearchOnlyGoldByDefault { get; set; } = false;
    public bool OnlyGoldTorrentsForNonAdmins { get; set; } = false;
    public List<string> DisabledTrackers { get; set; } = new();
    public List<TrackerRule> TrackerRules { get; set; } = new();
    public string DefaultLanguage { get; set; } = "en"; // "en" (default) or "uk"

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

    // Categories
    public List<CategoryMapping> Categories { get; set; } = new()
    {
        new CategoryMapping("Movies", "movies", "movie", new List<int> { 2000, 2010, 2020, 2030, 2040, 2045, 2050, 2060, 2070 }, true),
        new CategoryMapping("Shows", "shows", "show", new List<int> { 5000, 5010, 5020, 5030, 5040, 5045, 5050, 5060 }, true),
        new CategoryMapping("Cartoons", "cartoons", "movie", new List<int> { 2000, 5000 }, false),
        new CategoryMapping("Anime", "anime", "show", new List<int> { 5070 }, false)
    };

    public PluginConfiguration()
    {
    }
}
