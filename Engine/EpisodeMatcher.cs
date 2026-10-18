using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Torr2Jelly.Engine;

public class EpisodeInfo
{
    public int SeasonNumber { get; set; } = 1;
    public int EpisodeNumber { get; set; } = 1;
    public int? EndEpisodeNumber { get; set; }
    public bool IsMatched { get; set; }

    public string FormatEpisodeTag()
    {
        if (EndEpisodeNumber.HasValue && EndEpisodeNumber.Value > EpisodeNumber)
        {
            return $"S{SeasonNumber:D2}E{EpisodeNumber:D2}-E{EndEpisodeNumber.Value:D2}";
        }

        return $"S{SeasonNumber:D2}E{EpisodeNumber:D2}";
    }

    public string FormatSeasonFolder()
    {
        if (SeasonNumber == 0)
        {
            return "Season 00";
        }

        return $"Season {SeasonNumber:D2}";
    }
}

public static partial class EpisodeMatcher
{
    private static readonly string[] VideoExtensions =
    {
        ".mkv", ".mp4", ".avi", ".mov", ".m4v", ".webm", ".ts", ".wmv", ".iso"
    };

    [GeneratedRegex(@"(?i)[sS](\d{1,2})[eE](\d{1,3})(?:[\-–eE]+(\d{1,3}))?", RegexOptions.Compiled)]
    private static partial Regex StandardSeasonEpisodeRegex();

    [GeneratedRegex(@"(?i)\b(\d{1,2})x(\d{1,3})\b", RegexOptions.Compiled)]
    private static partial Regex XSeasonEpisodeRegex();

    [GeneratedRegex(@"(?i)\b(?:Season|Сезон)\s*(\d{1,2})\b", RegexOptions.Compiled)]
    private static partial Regex SeasonFolderRegex();

    [GeneratedRegex(@"(?i)\b(?:e|ep|episode|серія|серия)\s*(\d{1,3})\b", RegexOptions.Compiled)]
    private static partial Regex PlainEpisodeRegex();

    [GeneratedRegex(@"(?i)\b(?:Special|Specials|OVA|SP)\b", RegexOptions.Compiled)]
    private static partial Regex SpecialsRegex();

    [GeneratedRegex(@"(?i)\b(?:s\d{1,2}|season|\u0441\u0435\u0437\u043e\u043d|\d{1,2}x\d{1,2}|e\d{1,3}|ep\d{1,3}|\u0441\u0435\u0440\u0456\u0457|\u0441\u0435\u0440\u0438\u0438|\u0441\u0435\u0440\u0456\u044f|\u0441\u0435\u0440\u0438\u044f|\u0432\u0441\u0456\s+\u0441\u0435\u0440\u0456\u0457)\b", RegexOptions.Compiled)]
    private static partial Regex ShowTitleKeywordsRegex();

    public static bool IsVideoFile(string filename)
    {
        var ext = Path.GetExtension(filename);
        if (string.IsNullOrEmpty(ext))
        {
            return false;
        }

        foreach (var vExt in VideoExtensions)
        {
            if (ext.Equals(vExt, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsShowRelease(string title, IEnumerable<string>? filePaths = null)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        if (ShowTitleKeywordsRegex().IsMatch(title))
        {
            return true;
        }

        if (filePaths != null)
        {
            var videoFiles = filePaths.Where(IsVideoFile).ToList();
            if (videoFiles.Any(f => ParseEpisodeInfo(f).IsMatched))
            {
                return true;
            }
        }

        return false;
    }

    public static EpisodeInfo ParseEpisodeInfo(string relativeFilePath, int fallbackEpisode = 1)
    {
        var result = new EpisodeInfo
        {
            EpisodeNumber = fallbackEpisode,
            SeasonNumber = 1,
            IsMatched = false
        };

        var fileName = Path.GetFileName(relativeFilePath);
        var dirName = Path.GetDirectoryName(relativeFilePath) ?? string.Empty;

        // Check standard S01E02 or S01E01-E02 in filename
        var match = StandardSeasonEpisodeRegex().Match(fileName);
        if (match.Success)
        {
            result.SeasonNumber = int.Parse(match.Groups[1].Value);
            result.EpisodeNumber = int.Parse(match.Groups[2].Value);
            if (match.Groups[3].Success && int.TryParse(match.Groups[3].Value, out var endEp))
            {
                result.EndEpisodeNumber = endEp;
            }
            result.IsMatched = true;
            return result;
        }

        // Check 1x02 in filename
        match = XSeasonEpisodeRegex().Match(fileName);
        if (match.Success)
        {
            result.SeasonNumber = int.Parse(match.Groups[1].Value);
            result.EpisodeNumber = int.Parse(match.Groups[2].Value);
            result.IsMatched = true;
            return result;
        }

        // Check season from directory path
        var seasonMatch = SeasonFolderRegex().Match(dirName);
        if (seasonMatch.Success)
        {
            result.SeasonNumber = int.Parse(seasonMatch.Groups[1].Value);
            result.IsMatched = true;
        }
        else if (SpecialsRegex().IsMatch(dirName) || SpecialsRegex().IsMatch(fileName))
        {
            result.SeasonNumber = 0;
            result.IsMatched = true;
        }

        // Check plain episode number in filename (e.g. "Episode 05" or "Ep 05")
        var epMatch = PlainEpisodeRegex().Match(fileName);
        if (epMatch.Success)
        {
            result.EpisodeNumber = int.Parse(epMatch.Groups[1].Value);
            result.IsMatched = true;
        }

        return result;
    }
}
