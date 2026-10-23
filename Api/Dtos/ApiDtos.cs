using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
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

public class AddSeriesTorrentRequestDto
{
    public string Link { get; set; } = string.Empty;
    public string? TorrentTitle { get; set; }
}

/// <summary>Why <see cref="SeriesTorrentResultDto.Success"/> is false. Server-side only: never serialised.</summary>
public enum SeriesTorrentFailure
{
    None = 0,

    /// <summary>The torrent has no usable info-hash (TorrServer answered with something unexpected).</summary>
    InvalidHash,

    /// <summary>The series folder is missing, outside the managed roots, or is a root itself.</summary>
    InvalidTarget,

    /// <summary>The torrent is already registered as an alternate stream or to another folder, or lives elsewhere in the library.</summary>
    Conflict,

    /// <summary>The torrent's file list holds no video file.</summary>
    NoVideoFiles,

    /// <summary>Writing the .strm files failed (details are in the log).</summary>
    WriteFailed
}

public class SeriesTorrentResultDto
{
    public bool Success { get; set; }
    public string TorrentHash { get; set; } = string.Empty;

    /// <summary>Number of episode .strm files of this torrent that are in place in the series folder after the call.</summary>
    public int EpisodesWritten { get; set; }

    /// <summary>TorrServer has not loaded the file list yet: nothing was written now, the episodes appear on the next sync.</summary>
    public bool Pending { get; set; }

    public string? Message { get; set; }

    [JsonIgnore]
    public SeriesTorrentFailure Failure { get; set; }
}

/// <summary>Body of <c>POST Plugins/Torr2Jelly/Placeholders</c>: movies to create library placeholders for.</summary>
public class CreatePlaceholdersRequestDto
{
    public List<PlaceholderItemDto> Items { get; set; } = new();
}

public class PlaceholderItemDto
{
    public string Category { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    public string ImdbId { get; set; } = string.Empty;
    public int? TmdbId { get; set; }
}

public class CreatePlaceholdersResponseDto
{
    public List<PlaceholderResultDto> Results { get; set; } = new();
}

/// <summary>Per-movie outcome: <see cref="Status"/> is "created", "exists" or "error"; <see cref="Reason"/> is a fixed code.</summary>
public class PlaceholderResultDto
{
    public string ImdbId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Folder { get; set; }
    public string? Reason { get; set; }
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
