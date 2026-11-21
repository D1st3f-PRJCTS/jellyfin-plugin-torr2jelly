using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Torr2Jelly.Search;

/// <summary>
/// The external ids of one movie or series, for indexers that can be asked for a title by id instead of by name
/// (Prowlarr's <c>{ImdbId:...}</c> query tokens, Torznab's <c>t=movie&amp;imdbid=...</c>). An id search returns
/// exactly the releases the tracker has filed under that title, whatever language their names are in.
/// </summary>
public sealed record MediaSearchIds
{
    private static readonly Regex ImdbRegex = new(@"^tt[0-9]{1,10}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public MediaSearchIds(bool isSeries, string? imdbId, int? tmdbId, int? tvdbId = null)
    {
        IsSeries = isSeries;
        ImdbId = NormalizeImdb(imdbId);
        TmdbId = tmdbId is > 0 ? tmdbId : null;

        // TVDB ids only identify series.
        TvdbId = isSeries && tvdbId is > 0 ? tvdbId : null;
    }

    public bool IsSeries { get; }

    /// <summary>Lowercase <c>tt1234567</c>, or null.</summary>
    public string? ImdbId { get; }

    public int? TmdbId { get; }

    public int? TvdbId { get; }

    public bool HasAny => ImdbId != null || TmdbId.HasValue || TvdbId.HasValue;

    /// <summary>The digits of <see cref="ImdbId"/> without the <c>tt</c> prefix (the form Torznab expects), or null.</summary>
    public string? ImdbDigits => ImdbId?[2..];

    /// <summary><c>tt1234567</c> in lowercase when <paramref name="value"/> is an IMDb id, otherwise null.</summary>
    public static string? NormalizeImdb(string? value)
    {
        var text = value?.Trim().ToLowerInvariant();
        return text != null && ImdbRegex.IsMatch(text) ? text : null;
    }

    /// <summary>
    /// The one id an indexer is asked for. A single id keeps the request valid for indexers that support only some of
    /// them: IMDb first (the most widely supported), then TVDB for series, then TMDb.
    /// </summary>
    internal (string Name, string Value)? Preferred()
    {
        if (ImdbId != null)
        {
            return ("imdb", ImdbId);
        }

        if (TvdbId.HasValue)
        {
            return ("tvdb", TvdbId.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (TmdbId.HasValue)
        {
            return ("tmdb", TmdbId.Value.ToString(CultureInfo.InvariantCulture));
        }

        return null;
    }

    /// <summary>
    /// True when the ids of a search result name this title; false when one of them names another title; null when
    /// the result carries no id that can be compared.
    /// </summary>
    public bool? Matches(string? imdbId, int? tmdbId, int? tvdbId = null)
    {
        var otherImdb = NormalizeImdb(imdbId);
        bool? verdict = null;

        if (ImdbId != null && otherImdb != null)
        {
            if (!string.Equals(ImdbId, otherImdb, StringComparison.Ordinal))
            {
                return false;
            }

            verdict = true;
        }

        if (TmdbId.HasValue && tmdbId is > 0)
        {
            if (TmdbId.Value != tmdbId.Value)
            {
                return false;
            }

            verdict = true;
        }

        if (TvdbId.HasValue && tvdbId is > 0)
        {
            if (TvdbId.Value != tvdbId.Value)
            {
                return false;
            }

            verdict = true;
        }

        return verdict;
    }
}
