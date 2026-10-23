using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Torr2Jelly.Api.Dtos;
using Jellyfin.Plugin.Torr2Jelly.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torr2Jelly.Engine;

/// <summary>A TMDb failure with a stable code (<see cref="TmdbClient.CodeKeyMissing"/> and friends); never carries request details.</summary>
public sealed class TmdbException : Exception
{
    public TmdbException(string code)
        : base(code)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>One TMDb match plus the fields the title resolution needs that the public DTO does not carry.</summary>
public sealed class TmdbMatch
{
    public TmdbResultDto Dto { get; init; } = new();

    /// <summary>TMDb's <c>original_language</c> (e.g. "en", "ja"), or null.</summary>
    public string? OriginalLanguage { get; init; }
}

public sealed record TmdbExternalIds(string? ImdbId, int? TvdbId);

/// <summary>
/// Minimal TMDb v3 client: title search, external ids, an English title lookup and a key check. The API key is a
/// secret: it is never logged, never part of an exception, and request URLs are never logged (only the path).
/// </summary>
public class TmdbClient
{
    public const string CodeKeyMissing = "tmdb_key_missing";
    public const string CodeKeyInvalid = "tmdb_key_invalid";
    public const string CodeFailed = "tmdb_failed";

    public const int MaxResults = 12;

    private const string BaseUrl = "https://api.themoviedb.org/3";
    private const string ImageBase = "https://image.tmdb.org/t/p/w154";
    private const int MaxOverviewLength = 240;
    private const int MaxQueryLength = 200;
    private const int MaxCacheEntries = 300;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly ILogger<TmdbClient> _logger;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, (DateTime At, List<TmdbMatch> Items)> _cache = new(StringComparer.Ordinal);

    public TmdbClient(ILogger<TmdbClient> logger, IHttpClientFactory httpClientFactory)
        : this(logger, httpClientFactory.CreateClient())
    {
    }

    // Tests supply a client with a fake handler.
    internal TmdbClient(ILogger<TmdbClient> logger, HttpClient http)
    {
        _logger = logger;
        _http = http;
        _http.Timeout = RequestTimeout;
    }

    /// <summary>True when a key or token is stored in the configuration.</summary>
    public static bool HasStoredKey() => !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration?.TmdbApiKey);

    /// <summary>"uk-UA" when the configured default language is "uk", otherwise "en-US".</summary>
    public static string ConfiguredLanguage()
    {
        var language = Plugin.Instance?.Configuration?.DefaultLanguage;
        return string.Equals(language?.Trim(), "uk", StringComparison.OrdinalIgnoreCase) ? "uk-UA" : "en-US";
    }

    /// <summary>
    /// Searches movies and/or series. <paramref name="type"/> is "movie", "tv" or "all". At most
    /// <see cref="MaxResults"/> matches, sorted by TMDb relevance (movie and series lists interleaved by rank).
    /// Cached for ten minutes per (query, type, year, language).
    /// </summary>
    public async Task<List<TmdbMatch>> SearchAsync(
        string query,
        string type,
        int? year,
        string? language = null,
        CancellationToken ct = default)
    {
        var key = GetKey();
        language = string.IsNullOrWhiteSpace(language) ? ConfiguredLanguage() : language;
        query = (query ?? string.Empty).Trim();
        if (query.Length > MaxQueryLength)
        {
            query = query[..MaxQueryLength];
        }

        type = type switch
        {
            "movie" => "movie",
            "tv" or "series" => "tv",
            _ => "all"
        };

        if (query.Length == 0)
        {
            return new List<TmdbMatch>();
        }

        var cacheKey = string.Join('\u001f', query.ToLowerInvariant(), type, year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, language);
        if (_cache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.At < CacheTtl)
        {
            return cached.Items.ToList();
        }

        var movies = type != "tv" ? await SearchKindAsync(key, "movie", query, year, language, ct) : new List<TmdbMatch>();
        var series = type != "movie" ? await SearchKindAsync(key, "tv", query, year, language, ct) : new List<TmdbMatch>();

        var merged = new List<TmdbMatch>();
        for (var i = 0; i < Math.Max(movies.Count, series.Count); i++)
        {
            if (i < movies.Count) merged.Add(movies[i]);
            if (i < series.Count) merged.Add(series[i]);
        }

        merged = merged.Take(MaxResults).ToList();

        if (_cache.Count >= MaxCacheEntries)
        {
            foreach (var stale in _cache.Where(e => DateTime.UtcNow - e.Value.At >= CacheTtl).Select(e => e.Key).ToList())
            {
                _cache.TryRemove(stale, out _);
            }

            if (_cache.Count >= MaxCacheEntries)
            {
                _cache.Clear();
            }
        }

        _cache[cacheKey] = (DateTime.UtcNow, merged);
        return merged.ToList();
    }

    /// <summary>The IMDb and TVDB ids of a TMDb title. <paramref name="kind"/> is "movie" or "series".</summary>
    public async Task<TmdbExternalIds> GetExternalIdsAsync(string kind, int tmdbId, CancellationToken ct = default)
    {
        var key = GetKey();
        var path = $"/{(kind == "series" ? "tv" : "movie")}/{tmdbId.ToString(CultureInfo.InvariantCulture)}/external_ids";
        using var doc = await GetJsonAsync(key, path, null, ct);
        var root = doc.RootElement;

        var imdb = GetString(root, "imdb_id");
        if (imdb != null && !System.Text.RegularExpressions.Regex.IsMatch(imdb, @"^tt\d{1,10}$"))
        {
            imdb = null;
        }

        int? tvdb = null;
        if (root.TryGetProperty("tvdb_id", out var tvdbElement) && tvdbElement.ValueKind == JsonValueKind.Number &&
            tvdbElement.TryGetInt32(out var tvdbValue) && tvdbValue > 0)
        {
            tvdb = tvdbValue;
        }

        return new TmdbExternalIds(imdb, tvdb);
    }

    /// <summary>The English (en-US) title of a TMDb title, or null.</summary>
    public async Task<string?> GetEnglishTitleAsync(string kind, int tmdbId, CancellationToken ct = default)
    {
        var key = GetKey();
        var path = $"/{(kind == "series" ? "tv" : "movie")}/{tmdbId.ToString(CultureInfo.InvariantCulture)}";
        using var doc = await GetJsonAsync(key, path, new[] { ("language", "en-US") }, ct);
        return GetString(doc.RootElement, kind == "series" ? "name" : "title");
    }

    /// <summary>
    /// Checks a key with one search. Returns null when the key works, otherwise a code
    /// (<see cref="CodeKeyMissing"/>, <see cref="CodeKeyInvalid"/> or <see cref="CodeFailed"/>). Never throws for a bad key.
    /// </summary>
    public async Task<string?> TestAsync(string? apiKey, CancellationToken ct = default)
    {
        var key = string.IsNullOrWhiteSpace(apiKey) ? Plugin.Instance?.Configuration?.TmdbApiKey?.Trim() : apiKey.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return CodeKeyMissing;
        }

        try
        {
            using var doc = await GetJsonAsync(key, "/search/movie", new[] { ("query", "test"), ("page", "1") }, ct);
            return null;
        }
        catch (TmdbException ex)
        {
            return ex.Code;
        }
    }

    private static string GetKey()
    {
        var key = Plugin.Instance?.Configuration?.TmdbApiKey?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            throw new TmdbException(CodeKeyMissing);
        }

        return key;
    }

    private async Task<List<TmdbMatch>> SearchKindAsync(string key, string kind, string query, int? year, string language, CancellationToken ct)
    {
        var args = new List<(string, string)>
        {
            ("query", query),
            ("language", language),
            ("include_adult", "false"),
            ("page", "1")
        };

        if (year.HasValue)
        {
            args.Add((kind == "movie" ? "year" : "first_air_date_year", year.Value.ToString(CultureInfo.InvariantCulture)));
        }

        using var doc = await GetJsonAsync(key, "/search/" + kind, args, ct);
        var list = new List<TmdbMatch>();
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var item in results.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id) || id <= 0)
            {
                continue;
            }

            var isMovie = kind == "movie";
            var title = GetString(item, isMovie ? "title" : "name");
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var date = GetString(item, isMovie ? "release_date" : "first_air_date");
            int? itemYear = null;
            if (date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y > 0)
            {
                itemYear = y;
            }

            var overview = GetString(item, "overview");
            if (overview != null && overview.Length > MaxOverviewLength)
            {
                overview = overview[..MaxOverviewLength].TrimEnd() + "…";
            }

            var poster = GetString(item, "poster_path");
            list.Add(new TmdbMatch
            {
                OriginalLanguage = GetString(item, "original_language"),
                Dto = new TmdbResultDto
                {
                    TmdbId = id,
                    Kind = isMovie ? "movie" : "series",
                    Title = title,
                    OriginalTitle = GetString(item, isMovie ? "original_title" : "original_name"),
                    Year = itemYear,
                    Overview = string.IsNullOrWhiteSpace(overview) ? null : overview,
                    PosterUrl = !string.IsNullOrEmpty(poster) && poster.StartsWith('/') ? ImageBase + poster : null
                }
            });
        }

        return list;
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        return null;
    }

    // One GET. The result is a parsed document the caller disposes. Any problem becomes a TmdbException carrying only
    // a code; the request URL (which may hold the key) is never logged.
    private async Task<JsonDocument> GetJsonAsync(string key, string path, IEnumerable<(string Name, string Value)>? args, CancellationToken ct)
    {
        var isToken = key.StartsWith("eyJ", StringComparison.Ordinal);
        var url = new StringBuilder(BaseUrl).Append(path);
        var separator = '?';
        foreach (var (name, value) in args ?? Array.Empty<(string, string)>())
        {
            url.Append(separator).Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
            separator = '&';
        }

        if (!isToken)
        {
            url.Append(separator).Append("api_key=").Append(Uri.EscapeDataString(key));
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url.ToString());
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (isToken)
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                }

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    _logger.LogWarning("TMDb rejected the API key (HTTP {Status})", (int)response.StatusCode);
                    throw new TmdbException(CodeKeyInvalid);
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 0)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
                    await Task.Delay(wait > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : wait, ct);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("TMDb request {Path} failed with HTTP {Status}", path, (int)response.StatusCode);
                    throw new TmdbException(CodeFailed);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            }
            catch (TmdbException)
            {
                throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Only the type: the message of an HTTP exception can contain the request URL.
                _logger.LogWarning("TMDb request {Path} failed ({Error})", path, ex.GetType().Name);
                throw new TmdbException(CodeFailed);
            }
        }
    }
}
