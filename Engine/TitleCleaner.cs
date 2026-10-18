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

    [GeneratedRegex(@"(?i)\(?\b(сезон[иа]?|seasons?)\s*[\d\-\s,+–—]+\)?", RegexOptions.Compiled)]
    private static partial Regex SeasonNoiseRegex();

    [GeneratedRegex(@"(?i)\b(2160p|1080p|1080i|720p|480p|4k|uhd|x264|x265|h264|h265|hevc|avc|xvid|divx|10bit|hdr|hdr10\+?|dv|dovi|remux|bluray|blu-ray|web-?dl|webrip|hdtv|bdrip|dvdrip|proper|repack|imax|ddp?5\.1|dd5\.1|ac3|dts(-hd)?|truehd|atmos|aac\d?|flac|mp3)\b", RegexOptions.Compiled)]
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
        foreach (var ch in text)
        {
            var s = ch.ToString();
            if (TranslitMap.TryGetValue(s, out var replacement))
            {
                sb.Append(replacement);
            }
            else
            {
                sb.Append(ch);
            }
        }

        return MultipleSpacesRegex().Replace(sb.ToString(), " ").Trim();
    }

    public static string GetEnglishOrLatinTitle(string cleanTitle, string? originalTitle)
    {
        // 1. If original English title exists, prefer it
        if (!string.IsNullOrWhiteSpace(originalTitle) && originalTitle.Any(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')))
        {
            return originalTitle.Trim();
        }

        // 2. If cleanTitle is already Latin/English, return it
        var hasCyrillic = cleanTitle.Any(c => c >= 0x0400 && c <= 0x04FF);
        if (!hasCyrillic && !string.IsNullOrWhiteSpace(cleanTitle))
        {
            return cleanTitle.Trim();
        }

        // 3. If cleanTitle is Cyrillic, transliterate to Latin
        var translit = TransliterateUkrainianToLatin(cleanTitle);
        if (!string.IsNullOrWhiteSpace(translit))
        {
            return translit;
        }

        return cleanTitle;
    }

    public static (string CleanTitle, string? OriginalTitle, int? Year) CleanTitleAndExtractYear(string rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle))
        {
            return (string.Empty, null, null);
        }

        var text = rawTitle.Trim();

        // 1. Check for year range e.g. (1999-2003) or (2011-2019)
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

        // 2. Cut off everything from first quality / release noise token
        var noiseMatch = NoiseTokensRegex().Match(text);
        if (noiseMatch.Success && noiseMatch.Index > 2)
        {
            text = text.Substring(0, noiseMatch.Index);
        }

        // 3. Remove subtitle suffix e.g. "| Sub Ukr/Eng"
        text = SubtitleSuffixRegex().Replace(text, string.Empty);

        // 4. Remove square bracket content (tracker tags, groups, audio tags like [Hurtom], [3xUKR_ENG])
        var bracketMatches = SquareBracketContentRegex().Matches(text);
        if (bracketMatches.Count > 0)
        {
            var stripped = SquareBracketContentRegex().Replace(text, " ");
            if (stripped.Trim().Length >= 2)
            {
                text = stripped;
            }
        }

        // 5. Remove Season markers e.g. (Сезон 1-8), (Season 1-3)
        text = SeasonNoiseRegex().Replace(text, string.Empty);

        // 6. Remove audio language tags e.g. 4xUkr/Eng
        text = AudioLangsRegex().Replace(text, string.Empty);

        // 7. Find single year if not extracted from range
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

        // Replace dots and underscores with space
        text = DotsAndUnderscoresRegex().Replace(text, " ");

        // Clean edge punctuation
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
                var hasCyrillic0 = p0.Any(c => c >= 0x0400 && c <= 0x04FF);
                var hasCyrillic1 = p1.Any(c => c >= 0x0400 && c <= 0x04FF);

                if (hasCyrillic0 && !hasCyrillic1)
                {
                    cleanTitle = p0;
                    originalTitle = p1;
                }
                else if (!hasCyrillic0 && hasCyrillic1)
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

        if (string.IsNullOrWhiteSpace(cleanTitle))
        {
            cleanTitle = SanitizeFilename(rawTitle.Trim());
        }

        return (cleanTitle, originalTitle, extractedYear);
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
