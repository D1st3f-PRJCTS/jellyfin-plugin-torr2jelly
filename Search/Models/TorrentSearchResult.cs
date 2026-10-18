using System;
using System.Collections.Generic;

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
    public string DownloadUrl { get; set; } = string.Empty;
    public string MagnetUrl { get; set; } = string.Empty;
    public string InfoHash { get; set; } = string.Empty;
    public bool IsFreeleech { get; set; }
    public bool IsGold { get; set; }
    public DateTime? PublishDate { get; set; }
    public List<int> Categories { get; set; } = new();
    public string? PosterUrl { get; set; }
    public string? ImdbId { get; set; }
    public int? TmdbId { get; set; }

    public static string FormatBytes(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1)
        {
            number /= 1024;
            counter++;
        }
        return $"{number:n1} {suffixes[counter]}";
    }
}
