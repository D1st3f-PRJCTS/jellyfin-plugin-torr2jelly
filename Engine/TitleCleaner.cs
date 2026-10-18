using System;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Torr2Jelly.Engine;

public static partial class TitleCleaner
{
    [GeneratedRegex(@"\b(19\d\d|20\d\d)\b", RegexOptions.Compiled)]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"(?i)\b(2160p|1080p|1080i|720p|480p|4k|uhd|x264|x265|h264|h265|hevc|avc|xvid|divx|10bit|hdr|hdr10\+?|dv|dovi|remux|bluray|blu-ray|web-?dl|webrip|hdtv|bdrip|dvdrip|proper|repack|ddp?5\.1|dd5\.1|ac3|dts(-hd)?|truehd|atmos|aac\d?|flac|mp3)\b", RegexOptions.Compiled)]
    private static partial Regex NoiseTokensRegex();

    [GeneratedRegex(@"[\[\]\{\}]", RegexOptions.Compiled)]
    private static partial Regex BracketsRegex();

    [GeneratedRegex(@"[._]+", RegexOptions.Compiled)]
    private static partial Regex DotsAndUnderscoresRegex();

    [GeneratedRegex(@"\s{2,}", RegexOptions.Compiled)]
    private static partial Regex MultipleSpacesRegex();

    public static (string CleanTitle, int? Year) CleanTitleAndExtractYear(string rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle))
        {
            return (string.Empty, null);
        }

        var text = rawTitle.Trim();

        // Find year
        int? extractedYear = null;
        var yearMatches = YearRegex().Matches(text);
        if (yearMatches.Count > 0)
        {
            // Pick last reasonable year
            for (var i = yearMatches.Count - 1; i >= 0; i--)
            {
                if (int.TryParse(yearMatches[i].Value, out var y) && y >= 1900 && y <= DateTime.UtcNow.Year + 2)
                {
                    extractedYear = y;
                    break;
                }
            }
        }

        // Replace brackets and dots
        text = BracketsRegex().Replace(text, " ");
        text = DotsAndUnderscoresRegex().Replace(text, " ");

        // Cut off everything from first quality / release noise token
        var noiseMatch = NoiseTokensRegex().Match(text);
        if (noiseMatch.Success && noiseMatch.Index > 2)
        {
            text = text.Substring(0, noiseMatch.Index);
        }

        // If year was found, remove trailing year from title to avoid duplication
        if (extractedYear.HasValue)
        {
            var yearStr = extractedYear.Value.ToString();
            var yearIdx = text.LastIndexOf(yearStr, StringComparison.Ordinal);
            if (yearIdx > 0 && yearIdx >= text.Length - yearStr.Length - 3)
            {
                text = text.Substring(0, yearIdx);
            }
        }

        // Clean trailing punctuation
        text = text.Trim(' ', '-', '_', '.', '/', '\\', '(', ')');
        text = MultipleSpacesRegex().Replace(text, " ").Trim();

        if (string.IsNullOrWhiteSpace(text))
        {
            text = rawTitle.Trim();
        }

        return (text, extractedYear);
    }

    public static string FormatFolderTitle(string cleanTitle, int? year)
    {
        if (year.HasValue && !cleanTitle.Contains(year.Value.ToString(), StringComparison.Ordinal))
        {
            return $"{cleanTitle} ({year.Value})";
        }

        return cleanTitle;
    }

    public static string SanitizeFilename(string name)
    {
        var invalidChars = System.IO.Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalidChars, chars[i]) >= 0 || chars[i] == ':' || chars[i] == '?' || chars[i] == '*' || chars[i] == '"' || chars[i] == '<' || chars[i] == '>' || chars[i] == '|')
            {
                chars[i] = ' ';
            }
        }

        return MultipleSpacesRegex().Replace(new string(chars), " ").Trim();
    }
}
