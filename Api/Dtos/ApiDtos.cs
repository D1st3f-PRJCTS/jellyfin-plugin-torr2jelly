using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Torr2Jelly.Search.Models;

namespace Jellyfin.Plugin.Torr2Jelly.Api.Dtos;

public class AddTorrentRequestDto
{
    public string Link { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? Category { get; set; }
    public string? PosterUrl { get; set; }
    public int? TmdbId { get; set; }
    public string? ImdbId { get; set; }
    public bool SyncNow { get; set; } = true;
}

public class RecategorizeTorrentRequestDto
{
    public string Category { get; set; } = string.Empty;
    public string? CustomTitle { get; set; }
}

public class ServerLibraryDto
{
    public string Name { get; set; } = string.Empty;
    public string CollectionType { get; set; } = string.Empty;
    public string Type { get; set; } = "movie"; // "movie" or "show"
    public List<string> Locations { get; set; } = new();
    public string PrimaryLocation { get; set; } = string.Empty;
}

public class PluginStatusDto
{
    public bool IsDaemonRunning { get; set; }
    public string DaemonVersion { get; set; } = string.Empty;
    public string StatusMessage { get; set; } = string.Empty;
    public string EffectiveUrl { get; set; } = string.Empty;
    public string StreamBaseUrl { get; set; } = string.Empty;
    public int ActiveTorrentsCount { get; set; }
    public int CacheSizeMb { get; set; }
    public bool UseDiskCache { get; set; }
    public string DiskCachePath { get; set; } = string.Empty;
}

public class TestConnectionRequestDto
{
    public string Service { get; set; } = string.Empty; // "prowlarr", "torznab", "torrserver"
    public string? Url { get; set; }
    public string? ApiKey { get; set; }
}

public class AddAlternateStreamRequestDto
{
    public string Link { get; set; } = string.Empty;
    public Guid? TargetItemId { get; set; }
    public string? TargetFolderPath { get; set; }
    public string? QualityTag { get; set; }
    public int? Seeders { get; set; }
    public string? Tracker { get; set; }
    public bool ReplaceExisting { get; set; } = false;
    public string? OldTorrentHash { get; set; }
    public string? LocalizedTitle { get; set; }
    public string? OriginalTitle { get; set; }
    public int? Year { get; set; }
    public int? TmdbId { get; set; }
    public string? ImdbId { get; set; }
    public string? TorrentTitle { get; set; }
}

public class AlternateStreamResultDto
{
    public bool Success { get; set; }
    public string TorrentHash { get; set; } = string.Empty;
    public string StrmFilePath { get; set; } = string.Empty;
    public string VersionLabel { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
}

public class MovieStreamInfoDto
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string? TorrentHash { get; set; }
    public string VersionLabel { get; set; } = string.Empty;
    public bool IsPrimary { get; set; }
}

public class MovieAlternativesResponseDto
{
    public Guid ItemId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string OriginalTitle { get; set; } = string.Empty;
    public int? Year { get; set; }
    public string? ImdbId { get; set; }
    public int? TmdbId { get; set; }
    public List<MovieStreamInfoDto> ExistingStreams { get; set; } = new();
    public List<TorrentSearchResult> Alternatives { get; set; } = new();
}
