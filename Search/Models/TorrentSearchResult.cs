using System;
using System.Collections.Generic;
using System.Globalization;

namespace Jellyfin.Plugin.Torr2Jelly.Search.Models;

public class TorrentSearchResult
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public long Size { get; set; }
    public string SizeFormatted { get; set; } = string.Empty;
    public int Seeders { get; set; }
    public int Peers { get; set; }
    public string Indexer { get; set; } = string.Empty;

    /// <summary>Download link for the browser. Never contains an <c>apikey</c> parameter (see <see cref="ProwlarrLinks.StripApiKey"/>).</summary>
    public string DownloadUrl { get; set; } = string.Empty;

    /// <summary>Magnet link. Synthesised from <see cref="InfoHash"/> when the indexer supplied none.</summary>
    public string MagnetUrl { get; set; } = string.Empty;

    /// <summary>Lowercase hex info-hash, or empty when unknown.</summary>
    public string InfoHash { get; set; } = string.Empty;

    /// <summary>
    /// True only when the download does not count against the tracker's statistics: a download volume factor of
    /// exactly 0, or an exact <c>freeleech</c> flag. Half / silver / bronze / freeleech25|50|75 are not freeleech.
    /// </summary>
    public bool IsFreeleech { get; set; }

    /// <summary>The indexer's download volume factor (0 = free, 1 = full), when it reported one.</summary>
    public double? DownloadVolumeFactor { get; set; }

    public bool IsGold { get; set; }
    public List<string> Tags { get; set; } = new();
    public DateTime? PublishDate { get; set; }
    public List<int> Categories { get; set; } = new();
    public string? PosterUrl { get; set; }
    public string? ImdbId { get; set; }
    public int? TmdbId { get; set; }

    private static readonly string[] SizeSuffixes = { "B", "KB", "MB", "GB", "TB", "PB", "EB" };

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0)
        {
            bytes = 0;
        }

        int counter = 0;
        decimal number = bytes;
        // The suffix index is clamped so a huge value can never run past the array.
        while (number >= 1024m && counter < SizeSuffixes.Length - 1)
        {
            number /= 1024;
            counter++;
        }

        return string.Format(CultureInfo.InvariantCulture, "{0:0.0} {1}", number, SizeSuffixes[counter]);
    }
}
