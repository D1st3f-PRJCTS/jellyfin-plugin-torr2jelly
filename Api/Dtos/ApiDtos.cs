namespace Jellyfin.Plugin.Torr2Jelly.Api.Dtos;

public class AddTorrentRequestDto
{
    public string Link { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? Category { get; set; }
    public string? PosterUrl { get; set; }
    public int? TmdbId { get; set; }
    public bool SyncNow { get; set; } = true;
}

public class PluginStatusDto
{
    public bool IsDaemonRunning { get; set; }
    public int? DaemonPid { get; set; }
    public string DaemonVersion { get; set; } = string.Empty;
    public int DaemonPort { get; set; }
    public string Mode { get; set; } = "Embedded";
    public string StatusMessage { get; set; } = string.Empty;
    public string EffectiveUrl { get; set; } = string.Empty;
    public string StreamBaseUrl { get; set; } = string.Empty;
    public int ActiveTorrentsCount { get; set; }
}

public class TestConnectionRequestDto
{
    public string Service { get; set; } = string.Empty; // "prowlarr", "torznab", "torrserver"
    public string? Url { get; set; }
    public string? ApiKey { get; set; }
}
