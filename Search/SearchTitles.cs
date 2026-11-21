using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.Torr2Jelly.Search;

/// <summary>Turning the titles of a library item into indexer queries, and finding those titles in release names.</summary>
internal static class SearchTitles
{
    private const int MaxTitles = 3;

    /// <summary>Stands for a separator of a release name's parts in <see cref="Tokenize"/> output (compared by reference).</summary>
    public static readonly string Boundary = new('\u0001', 1);

    /// <summary>
    /// True when <paramref name="title"/> is written in a script the indexers' release names use (Latin or
    /// Cyrillic). A title in another script (Japanese, Korean, Arabic, ...) matches nothing useful: trackers then
    /// answer with whatever fits the rest of the query, typically every release of that year.
    /// </summary>
    public static bool IsSearchable(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var letters = 0;
        foreach (var ch in title)
        {
            if (!char.IsLetter(ch))
            {
                continue;
            }

            if (!IsLatinOrCyrillic(ch))
            {
                return false;
            }

            letters++;
        }

        // Titles made of digits only ("1917", "2012") are searchable as they are.
        return letters > 0 || title.Any(char.IsDigit);
    }

    private static bool IsLatinOrCyrillic(char ch)
    {
        return ch <= 'ɏ'                      // Basic Latin .. Latin Extended-B
            || (ch >= 'Ḁ' && ch <= 'ỿ')  // Latin Extended Additional
            || (ch >= 'Ѐ' && ch <= 'ԯ'); // Cyrillic, Cyrillic Supplement
    }

    /// <summary>The distinct titles of an item that can be searched for, in the order given (at most three).</summary>
    public static List<string> Searchable(IEnumerable<string?> titles)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var title in titles)
        {
            var text = title?.Trim();
            if (!IsSearchable(text) || !seen.Add(text!))
            {
                continue;
            }

            result.Add(text!);
            if (result.Count == MaxTitles)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// The queries to send for an item, in the order to try them: one per distinct searchable title, with the year
    /// appended when one is given.
    /// </summary>
    public static List<string> BuildQueries(IEnumerable<string?> titles, int? year)
    {
        return Searchable(titles).Select(title => WithYear(title, year)).ToList();
    }

    public static string WithYear(string title, int? year)
    {
        return year is > 0 ? title + " " + year.Value.ToString(CultureInfo.InvariantCulture) : title;
    }

    /// <summary>
    /// The words of a title or release name: lowercase, without diacritics and apostrophes, split at everything that
    /// is not a letter or a digit ("Spider-Man: Brand New Day" gives spider, man, brand, new, day). With
    /// <paramref name="keepBoundaries"/>, brackets, slashes and pipes (the places where a release name ends one of
    /// its parts) are kept as <see cref="Boundary"/> entries.
    /// </summary>
    public static string[] Tokenize(string? text, bool keepBoundaries = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        var tokens = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        foreach (var ch in text.Normalize(NormalizationForm.FormKD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark || ch is '\'' or '\u2019' or '`')
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                current.Append(char.ToLowerInvariant(ch));
                continue;
            }

            Flush();
            if (keepBoundaries && ch is '(' or ')' or '[' or ']' or '/' or '|' &&
                (tokens.Count == 0 || !ReferenceEquals(tokens[^1], Boundary)))
            {
                tokens.Add(Boundary);
            }
        }

        Flush();
        return tokens.ToArray();
    }

    /// <summary>
    /// How a release name relates to the titles of an item (each given as its words): <see cref="TitleMatch.Exact"/>
    /// when one of them is a whole part of the name (it starts the name or follows a separator, and is followed by a
    /// separator, a year, a season marker or the end: "House of the Dragon (S3) (2026)" for "House of the Dragon",
    /// but not for "House"); <see cref="TitleMatch.Contained"/> when its words merely appear next to each other;
    /// otherwise <see cref="TitleMatch.None"/>.
    /// </summary>
    public static TitleMatch Match(string? releaseTitle, IReadOnlyList<string[]> phrases)
    {
        var words = Tokenize(releaseTitle, keepBoundaries: true);
        var best = TitleMatch.None;

        foreach (var phrase in phrases)
        {
            if (phrase.Length == 0)
            {
                continue;
            }

            for (var start = 0; start + phrase.Length <= words.Length; start++)
            {
                var matches = true;
                for (var i = 0; i < phrase.Length; i++)
                {
                    if (!string.Equals(words[start + i], phrase[i], StringComparison.Ordinal))
                    {
                        matches = false;
                        break;
                    }
                }

                if (!matches)
                {
                    continue;
                }

                var end = start + phrase.Length;
                var startsPart = start == 0 || ReferenceEquals(words[start - 1], Boundary);
                var endsPart = end == words.Length || EndsTitle(words[end]);
                if (startsPart && endsPart)
                {
                    return TitleMatch.Exact;
                }

                best = TitleMatch.Contained;
            }
        }

        return best;
    }

    /// <summary>True when the release name carries <paramref name="year"/> as a number of its own ("(2004-2012)" carries both).</summary>
    public static bool ContainsYear(string? releaseTitle, int? year)
    {
        if (year is not > 0)
        {
            return false;
        }

        var text = year.Value.ToString(CultureInfo.InvariantCulture);
        return Tokenize(releaseTitle).Any(word => string.Equals(word, text, StringComparison.Ordinal));
    }

    // What may follow a title inside a release name without being part of the title.
    private static bool EndsTitle(string word)
    {
        if (ReferenceEquals(word, Boundary))
        {
            return true;
        }

        // A year ("House 2004"), but not a sequel number ("Rocky 2").
        if (word.Length == 4 && int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year is >= 1900 and <= 2099)
        {
            return true;
        }

        if (word is "season" or "seasons" or "complete" || word.StartsWith("сезон", StringComparison.Ordinal))
        {
            return true;
        }

        // s01, s01e02
        if (word.Length >= 2 && word[0] == 's' && char.IsAsciiDigit(word[1]))
        {
            var rest = word.AsSpan(1);
            var digits = 0;
            while (digits < rest.Length && char.IsAsciiDigit(rest[digits]))
            {
                digits++;
            }

            rest = rest[digits..];
            return rest.Length == 0 || (rest[0] == 'e' && rest.Length > 1 && rest[1..].ToString().All(char.IsAsciiDigit));
        }

        return false;
    }
}

internal enum TitleMatch
{
    None = 0,
    Contained = 1,
    Exact = 2
}
