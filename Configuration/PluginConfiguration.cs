using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Torr2Jelly.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    // Embedded Standalone TorrServer Daemon settings
    public bool UseEmbeddedTorrServer { get; set; } = true;
    public int EmbeddedTorrServerPort { get; set; } = 8095;
    public string ExternalTorrServerUrl { get; set; } = "http://127.0.0.1:8090";
    public string TorrServerStreamUrl { get; set; } = string.Empty; // URL embedded into .strm files

    // Torrent Cache & Performance
    public int CacheSizeMb { get; set; } = 256;
    public int PreloadCachePercent { get; set; } = 50;
    public int ReaderReadAHeadPercent { get; set; } = 95;
    public bool UseDiskCache { get; set; } = false;
    public string DiskCachePath { get; set; } = string.Empty;

    // Search Indexer (Prowlarr / Torznab)
    public bool EnableProwlarr { get; set; } = true;
    public string ProwlarrUrl { get; set; } = "http://127.0.0.1:9696";
    public string ProwlarrApiKey { get; set; } = string.Empty;

    public bool EnableTorznab { get; set; } = false;
    public string TorznabUrl { get; set; } = string.Empty;
    public string TorznabApiKey { get; set; } = string.Empty;

    // Permissions & Filters
    public bool OnlyGoldTorrentsForNonAdmins { get; set; } = false;
    public string DefaultLanguage { get; set; } = "uk"; // "uk" or "en"

    // Library & Storage
    public string MediaRootPath { get; set; } = string.Empty;
    public bool AutoSyncTorrents { get; set; } = true;
    public int AutoSyncIntervalMinutes { get; set; } = 15;

    // Categories
    public List<CategoryMapping> Categories { get; set; } = new()
    {
        new CategoryMapping("Movies", "movies", "movie", new List<int> { 2000, 2010, 2020, 2030, 2040, 2045, 2050, 2060, 2070 }, true),
        new CategoryMapping("TV Shows", "shows", "show", new List<int> { 5000, 5010, 5020, 5030, 5040, 5045, 5050, 5060 }, true),
        new CategoryMapping("Anime", "anime", "show", new List<int> { 5070 }, false)
    };

    public PluginConfiguration()
    {
    }
}
