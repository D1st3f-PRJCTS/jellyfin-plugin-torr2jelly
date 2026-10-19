using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Torr2Jelly.Engine;

public static partial class TitleCleaner
{
    [GeneratedRegex(@"\b(19\d\d|20\d\d)\b", RegexOptions.Compiled)]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"(?i)\(?\b(19\d\d|20\d\d)\s*[-–—]\s*(19\d\d|20\d\d)\b\)?", RegexOptions.Compiled)]
    private static partial Regex YearRangeRegex();

    [GeneratedRegex(@"(?i)\(?\b(сезон[иа]?|seasons?)\s*[\d\-\s,+–—]+\)?|\(?\bS\d+(?:\s*[-–—]\s*(?:S)?\d+)?\)?", RegexOptions.Compiled)]
    private static partial Regex SeasonNoiseRegex();

    [GeneratedRegex(@"(?i)\b(2160p|1080p|1080i|720p|576p|480p|4k|uhd|8k|bdremux|uhdremux|remux|bluray(-?remux)?|blu-ray(-?remux)?|bdrip|brrip|dvdrip|web-?dlrip|web-?rip|web-?dl|hdtvrip|hdtv|tvrip|satrip|camrip|telesync|x264|x265|h264|h265|hevc|avc|av1|xvid|divx|10bit|hdr|hdr10\+?|dv|dovi|dolby\s*vision|proper|repack|rerip|imax|ddp?5\.1|dd5\.1|ac3|dts(-hd)?|truehd|atmos|aac\d?|flac|mp3)\b", RegexOptions.Compiled)]
    private static partial Regex NoiseTokensRegex();

    [GeneratedRegex(@"\[[^\]]*\]", RegexOptions.Compiled)]
    private static partial Regex SquareBracketContentRegex();

    [GeneratedRegex(@"[._]+", RegexOptions.Compiled)]
    private static partial Regex DotsAndUnderscoresRegex();

    [GeneratedRegex(@"\s{2,}", RegexOptions.Compiled)]
    private static partial Regex MultipleSpacesRegex();

    [GeneratedRegex(@"(?i)\b(\d+x)?(ukr|eng|rus|pol|ger|fra)(\/(ukr|eng|rus|pol|ger|fra))*\b", RegexOptions.Compiled)]
    private static partial Regex AudioLangsRegex();

    [GeneratedRegex(@"(?i)\|\s*Sub\b.*$", RegexOptions.Compiled)]
    private static partial Regex SubtitleSuffixRegex();

    private static readonly Dictionary<string, string> TranslitMap = new(StringComparer.Ordinal)
    {
        ["А"] = "A", ["Б"] = "B", ["В"] = "V", ["Г"] = "H", ["Ґ"] = "G",
        ["Д"] = "D", ["Е"] = "E", ["Є"] = "Ye", ["Ж"] = "Zh", ["З"] = "Z",
        ["И"] = "Y", ["І"] = "I", ["Ї"] = "Yi", ["Й"] = "Y", ["К"] = "K",
        ["Л"] = "L", ["М"] = "M", ["Н"] = "N", ["О"] = "O", ["П"] = "P",
        ["Р"] = "R", ["С"] = "S", ["Т"] = "T", ["У"] = "U", ["Ф"] = "F",
        ["Х"] = "Kh", ["Ц"] = "Ts", ["Ч"] = "Ch", ["Ш"] = "Sh", ["Щ"] = "Shch",
        ["Ю"] = "Yu", ["Я"] = "Ya", ["ь"] = "", ["Ь"] = "", ["'"] = "", ["’"] = "",
        ["а"] = "a", ["б"] = "b", ["в"] = "v", ["г"] = "h", ["ґ"] = "g",
        ["д"] = "d", ["е"] = "e", ["є"] = "ye", ["ж"] = "zh", ["з"] = "z",
        ["и"] = "y", ["і"] = "i", ["ї"] = "yi", ["й"] = "y", ["к"] = "k",
        ["л"] = "l", ["м"] = "m", ["н"] = "n", ["о"] = "o", ["п"] = "p",
        ["р"] = "r", ["с"] = "s", ["т"] = "t", ["у"] = "u", ["ф"] = "f",
        ["х"] = "kh", ["ц"] = "ts", ["ч"] = "ch", ["ш"] = "sh", ["щ"] = "shch",
        ["ю"] = "yu", ["я"] = "ya"
    };

    public static string TransliterateUkrainianToLatin(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var key = c.ToString();
            if (TranslitMap.TryGetValue(key, out var latin))
            {
                sb.Append(latin);
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    public static string GetEnglishOrLatinTitle(string cleanTitle, string? originalTitle)
    {
        if (!string.IsNullOrWhiteSpace(originalTitle) && !originalTitle.Any(c => c >= 0x0400 && c <= 0x04FF))
        {
            return originalTitle.Trim();
        }

        if (!string.IsNullOrWhiteSpace(cleanTitle) && !cleanTitle.Any(c => c >= 0x0400 && c <= 0x04FF))
        {
            return cleanTitle.Trim();
        }

        var translit = TransliterateUkrainianToLatin(cleanTitle);
        return !string.IsNullOrWhiteSpace(translit) ? translit.Trim() : cleanTitle.Trim();
    }

    public static (string CleanTitle, string? OriginalTitle, int? Year) CleanTitleAndExtractYear(string rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle))
        {
            return (string.Empty, null, null);
        }

        var text = rawTitle.Trim();

        // 1. Check for year range first e.g. (1994-2004) or 1994-2004
        int? extractedYear = null;
        var yearRangeMatch = YearRangeRegex().Match(text);
        if (yearRangeMatch.Success)
        {
            if (int.TryParse(yearRangeMatch.Groups[1].Value, out var startYear) && startYear >= 1900 && startYear <= DateTime.UtcNow.Year + 2)
            {
                extractedYear = startYear;
            }
            text = text.Remove(yearRangeMatch.Index, yearRangeMatch.Length);
        }

        // 2. Pre-extract single year before noise cutoff so years after quality/noise tokens are captured
        if (!extractedYear.HasValue)
        {
            var yearMatches = YearRegex().Matches(text);
            if (yearMatches.Count > 0)
            {
                for (var i = yearMatches.Count - 1; i >= 0; i--)
                {
                    if (int.TryParse(yearMatches[i].Value, out var y) && y >= 1900 && y <= DateTime.UtcNow.Year + 2)
                    {
                        extractedYear = y;
                        break;
                    }
                }
            }
        }

        // 3. Cut off everything from first quality / release noise token
        var noiseMatch = NoiseTokensRegex().Match(text);
        if (noiseMatch.Success && noiseMatch.Index > 2)
        {
            text = text.Substring(0, noiseMatch.Index);
        }

        // 4. Remove subtitle suffix e.g. "| Sub Ukr/Eng"
        text = SubtitleSuffixRegex().Replace(text, string.Empty);

        // 5. Remove square bracket content (tracker tags, groups, audio tags like [Hurtom], [3xUKR_ENG])
        var bracketMatches = SquareBracketContentRegex().Matches(text);
        if (bracketMatches.Count > 0)
        {
            var stripped = SquareBracketContentRegex().Replace(text, " ");
            if (stripped.Trim().Length >= 2)
            {
                text = stripped;
            }
        }

        // 6. Remove Season markers e.g. (Сезон 1-8), (Season 1-3), (S1-8)
        text = SeasonNoiseRegex().Replace(text, string.Empty);

        // 7. Remove audio language tags e.g. 4xUkr/Eng
        text = AudioLangsRegex().Replace(text, string.Empty);

        // Remove extracted year string from title text
        if (extractedYear.HasValue)
        {
            var yearStr = extractedYear.Value.ToString();
            var yearIdx = text.LastIndexOf(yearStr, StringComparison.Ordinal);
            if (yearIdx >= 0)
            {
                text = text.Remove(yearIdx, yearStr.Length);
            }
        }

        // Clean dots and underscores
        text = DotsAndUnderscoresRegex().Replace(text, " ");
        text = text.Trim(' ', '-', '_', '.', '/', '\\', '(', ')', ':');
        text = MultipleSpacesRegex().Replace(text, " ").Trim();

        // 8. Handle dual titles separated by "/" or "|"
        // e.g. "Матриця: Воскресіння / The Matrix Resurrections"
        string cleanTitle = text;
        string? originalTitle = null;

        var slashParts = text.Split(new[] { '/', '|' }, StringSplitOptions.RemoveEmptyEntries);
        if (slashParts.Length >= 2)
        {
            var p0 = MultipleSpacesRegex().Replace(slashParts[0].Trim(' ', '-', '_', '.', '(', ')'), " ").Trim();
            var p1 = MultipleSpacesRegex().Replace(slashParts[1].Trim(' ', '-', '_', '.', '(', ')'), " ").Trim();

            if (!string.IsNullOrWhiteSpace(p0) && !string.IsNullOrWhiteSpace(p1))
            {
                var p0HasCyr = p0.Any(c => c >= 0x0400 && c <= 0x04FF);
                var p1HasCyr = p1.Any(c => c >= 0x0400 && c <= 0x04FF);

                if (p0HasCyr && !p1HasCyr)
                {
                    cleanTitle = p0;
                    originalTitle = p1;
                }
                else if (!p0HasCyr && p1HasCyr)
                {
                    cleanTitle = p1;
                    originalTitle = p0;
                }
                else
                {
                    cleanTitle = p0;
                    originalTitle = p1;
                }
            }
        }

        return (cleanTitle, originalTitle, extractedYear);
    }

    public static string FormatFolderTitle(string cleanTitle, int? year)
    {
        if (string.IsNullOrWhiteSpace(cleanTitle))
        {
            return year.HasValue ? $"({year.Value})" : string.Empty;
        }

        if (year.HasValue && !cleanTitle.Contains(year.Value.ToString(), StringComparison.Ordinal))
        {
            return $"{cleanTitle} ({year.Value})";
        }

        return cleanTitle;
    }

    public static string SanitizeFilename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var invalidChars = System.IO.Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);

        foreach (var c in name)
        {
            if (c == ':' || c == '/' || c == '\\' || c == '|' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>')
            {
                sb.Append(' ');
            }
            else if (!invalidChars.Contains(c))
            {
                sb.Append(c);
            }
        }

        return MultipleSpacesRegex().Replace(sb.ToString(), " ").Trim(' ', '.', '-');
    }

    public static string ExtractQualityTag(string rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle))
        {
            return "1080p";
        }

        var matches = NoiseTokensRegex().Matches(rawTitle);
        if (matches.Count == 0)
        {
            return "1080p";
        }

        var res = string.Empty;
        var hasRemux = false;
        var hasHdr = false;
        var hasDv = false;
        var is4K = false;

        foreach (Match m in matches)
        {
            var val = m.Value.ToLowerInvariant();
            if (val is "2160p" or "4k" or "uhd")
            {
                res = "2160p";
                is4K = true;
            }
            else if (string.IsNullOrEmpty(res) && val is "1080p" or "1080i")
            {
                res = "1080p";
            }
            else if (string.IsNullOrEmpty(res) && val is "720p")
            {
                res = "720p";
            }

            if (val.Contains("remux") || val.Contains("bdremux") || val.Contains("uhdremux"))
            {
                hasRemux = true;
            }
            if (val is "hdr" or "hdr10" or "hdr10+")
            {
                hasHdr = true;
            }
            if (val is "dv" or "dovi" or "dolby vision")
            {
                hasDv = true;
            }
        }

        if (string.IsNullOrEmpty(res))
        {
            res = "1080p";
        }

        var parts = new List<string> { res };
        if (is4K && !parts.Contains("4K"))
        {
            parts.Add("4K");
        }
        if (hasDv)
        {
            parts.Add("DV");
        }
        if (hasHdr && !hasDv)
        {
            parts.Add("HDR");
        }
        if (hasRemux)
        {
            parts.Add("Remux");
        }

        return string.Join(" ", parts);
    }
}
