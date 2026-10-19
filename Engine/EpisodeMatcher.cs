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

    /// <summary>True when the episode number was read from the path (S01E02, 1x02, "Episode 5"), not guessed.</summary>
    public bool HasEpisodeNumber { get; set; }

    /// <summary>True when the season was read from the file name or from a season directory (not defaulted / "Specials").</summary>
    public bool HasSeason { get; set; }

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

    // S01E02, S01E02E03, S01E02-E03, S01E02-03. The optional end episode must not be followed by a digit or by a
    // resolution marker, so "S01E01-720p" / "S01E01-1080p" / "S01E01-4K" stay a single episode (E01).
    [GeneratedRegex(@"(?i)S(\d{1,2})E(\d{1,3})(?:(?:[\-–]E?|E)(\d{1,3})(?![\dpik]))?", RegexOptions.Compiled)]
    private static partial Regex StandardSeasonEpisodeRegex();

    [GeneratedRegex(@"(?i)\b(\d{1,2})x(\d{1,3})\b", RegexOptions.Compiled)]
    private static partial Regex XSeasonEpisodeRegex();

    // "Season 2", "Season.2", "Season_2", "Season02", "Сезон 2" (in a single directory name).
    [GeneratedRegex(@"(?i)(?<![\p{L}\d])(?:Season|Сезон)[\s._\-]*(\d{1,2})(?!\d)", RegexOptions.Compiled)]
    private static partial Regex SeasonFolderRegex();

    // A bare season token inside a directory name: "S02", "Show.Name.S02.1080p". Not "DTS5.1" and not "S02E01".
    [GeneratedRegex(@"(?i)(?<![\p{L}\d])S(\d{1,2})(?![\p{L}\d])", RegexOptions.Compiled)]
    private static partial Regex SeasonTokenRegex();

    [GeneratedRegex(@"(?i)\b(?:e|ep|episode|серія|серия)\s*(\d{1,3})\b", RegexOptions.Compiled)]
    private static partial Regex PlainEpisodeRegex();

    [GeneratedRegex(@"(?i)\b(?:Special|Specials|OVA|SP)\b", RegexOptions.Compiled)]
    private static partial Regex SpecialsRegex();

    // Title-level hints that a release is a TV show. "season"/"сезон" only count when followed by a number, so
    // "Season of the Witch" or "Сезон дощів" are not shows.
    [GeneratedRegex(@"(?i)\b(?:s\d{1,2}(?:e\d{1,3})?|(?:seasons?|сезон(?:и|а|у|ів)?)[\s._\-]*\d{1,2}|\d{1,2}x\d{1,2}|e\d{1,3}|ep\d{1,3}|серії|серии|серія|серия|всі\s+серії)\b", RegexOptions.Compiled)]
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
            foreach (var file in filePaths.Where(IsVideoFile))
            {
                // A "Special"/"OVA" word in a file name alone does not make a release a show.
                var info = ParseEpisodeInfo(file);
                if (info.HasEpisodeNumber || info.HasSeason)
                {
                    return true;
                }
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

        var normalizedPath = (relativeFilePath ?? string.Empty).Replace('\\', '/');
        var slashIdx = normalizedPath.LastIndexOf('/');
        var fileName = slashIdx >= 0 ? normalizedPath[(slashIdx + 1)..] : normalizedPath;
        var dirName = slashIdx >= 0 ? normalizedPath[..slashIdx] : string.Empty;

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
            result.HasSeason = true;
            result.HasEpisodeNumber = true;
            return result;
        }

        // Check 1x02 in filename
        match = XSeasonEpisodeRegex().Match(fileName);
        if (match.Success)
        {
            result.SeasonNumber = int.Parse(match.Groups[1].Value);
            result.EpisodeNumber = int.Parse(match.Groups[2].Value);
            result.IsMatched = true;
            result.HasSeason = true;
            result.HasEpisodeNumber = true;
            return result;
        }

        // Season from the directory path: the deepest directory that names a season wins.
        var directorySeason = FindSeasonInDirectories(dirName);
        if (directorySeason.HasValue)
        {
            result.SeasonNumber = directorySeason.Value;
            result.IsMatched = true;
            result.HasSeason = true;
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
            result.HasEpisodeNumber = true;
        }

        return result;
    }

    /// <summary>
    /// Gives every file that has no explicit episode number the next free number of its own season
    /// (1, 2, 3 ... per season, in the given order), skipping numbers that explicit episodes already use.
    /// </summary>
    public static void AssignFallbackEpisodeNumbers(IReadOnlyList<EpisodeInfo> infos)
    {
        var used = new Dictionary<int, HashSet<int>>();
        foreach (var info in infos.Where(i => i.HasEpisodeNumber))
        {
            if (!used.TryGetValue(info.SeasonNumber, out var set))
            {
                used[info.SeasonNumber] = set = new HashSet<int>();
            }

            set.Add(info.EpisodeNumber);
        }

        var lastAssigned = new Dictionary<int, int>();
        foreach (var info in infos)
        {
            if (info.HasEpisodeNumber)
            {
                continue;
            }

            if (!used.TryGetValue(info.SeasonNumber, out var set))
            {
                used[info.SeasonNumber] = set = new HashSet<int>();
            }

            lastAssigned.TryGetValue(info.SeasonNumber, out var last);
            var next = last + 1;
            while (set.Contains(next))
            {
                next++;
            }

            info.EpisodeNumber = next;
            set.Add(next);
            lastAssigned[info.SeasonNumber] = next;
        }
    }

    private static int? FindSeasonInDirectories(string dirName)
    {
        if (string.IsNullOrEmpty(dirName))
        {
            return null;
        }

        var segments = dirName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            var segment = segments[i];

            var m = SeasonFolderRegex().Match(segment);
            if (!m.Success)
            {
                m = SeasonTokenRegex().Match(segment);
            }

            if (m.Success && int.TryParse(m.Groups[1].Value, out var season))
            {
                return season;
            }
        }

        return null;
    }
}
