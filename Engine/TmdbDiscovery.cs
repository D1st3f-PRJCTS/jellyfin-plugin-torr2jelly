using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Torr2Jelly.Api.Dtos;

namespace Jellyfin.Plugin.Torr2Jelly.Engine;

public partial class TmdbClient
{
    // Documents, not mutable DTOs, are cached; callers always get their own objects.
    private readonly Dictionary<string, (DateTime At, string Json)> _discoveryCache = new();
    private readonly SemaphoreSlim _discoveryLock = new(1, 1);

    private async Task<JsonDocument> GetDiscoveryJsonAsync(string path, string? append, CancellationToken ct)
    {
        var key = GetKey();
        var language = ConfiguredLanguage();
        var cacheKey = path + ":" + language;
        await _discoveryLock.WaitAsync(ct);
        try
        {
            if (_discoveryCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.At < CacheTtl)
                return JsonDocument.Parse(cached.Json);
            var args = new List<(string, string)> { ("language", language) };
            if (append != null) args.Add(("append_to_response", append));
            using var doc = await GetJsonAsync(key, path, args, ct);
            if (_discoveryCache.Count >= MaxCacheEntries) _discoveryCache.Clear();
            var json = doc.RootElement.GetRawText();
            _discoveryCache[cacheKey] = (DateTime.UtcNow, json);
            return JsonDocument.Parse(json);
        }
        finally { _discoveryLock.Release(); }
    }

    public async Task<List<DiscoveryMovieDto>> GetTrendingMoviesAsync(string window, CancellationToken ct = default)
    {
        if (window is not ("day" or "week")) throw new ArgumentException("Invalid time window.", nameof(window));
        using var doc = await GetDiscoveryJsonAsync("/trending/movie/" + window, null, ct);
        return JsonArray(doc.RootElement, "results").Where(e => !IsTrue(e, "adult") && Int(e, "id") > 0)
            .Take(20).Select(MapDiscoveryMovie).ToList();
    }

    public async Task<DiscoveryMovieDto> GetMovieDetailsAsync(int id, CancellationToken ct = default)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        using var doc = await GetDiscoveryJsonAsync("/movie/" + id.ToString(CultureInfo.InvariantCulture), "credits,alternative_titles", ct);
        var root = doc.RootElement;
        if (Int(root, "id") != id || IsTrue(root, "adult")) throw new TmdbException(CodeFailed);
        var movie = MapDiscoveryMovie(root);
        var imdb = GetString(root, "imdb_id");
        movie.ImdbId = imdb != null && Regex.IsMatch(imdb, "^tt[0-9]{1,10}$") ? imdb : null;
        movie.Runtime = Int(root, "runtime") > 0 ? Int(root, "runtime") : null;
        movie.IsAnimation = JsonArray(root, "genres").Any(e => Int(e, "id") == 16);
        movie.Genres = JsonArray(root, "genres").Select(e => GetString(e, "name")).OfType<string>().ToList();
        if (root.TryGetProperty("credits", out var credits))
            movie.Actors = JsonArray(credits, "cast").Take(12).Select(e => new DiscoveryActorDto(
                GetString(e, "name") ?? string.Empty, GetString(e, "character"), Image(e, "profile_path", "w185"))).ToList();
        movie.Titles = new[] { movie.Title, movie.OriginalTitle }.OfType<string>().ToList();
        if (root.TryGetProperty("alternative_titles", out var titles))
            movie.Titles.AddRange(JsonArray(titles, "titles").Select(e => GetString(e, "title")).OfType<string>());
        movie.Titles = movie.Titles.Where(s => s.Length is > 0 and <= 512).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToList();
        return movie;
    }

    private static DiscoveryMovieDto MapDiscoveryMovie(JsonElement e)
    {
        var date = GetString(e, "release_date");
        int? year = date?.Length >= 4 && int.TryParse(date.AsSpan(0, 4), out var y) ? y : null;
        var votes = Int(e, "vote_count");
        double? rating = e.TryGetProperty("vote_average", out var score) && score.TryGetDouble(out var n) && n is >= 0 and <= 10 && votes > 0 ? n : null;
        return new DiscoveryMovieDto
        {
            TmdbId = Int(e, "id"), Title = GetString(e, "title") ?? string.Empty,
            OriginalTitle = GetString(e, "original_title"), Year = year,
            Overview = GetString(e, "overview"), PosterUrl = Image(e, "poster_path", "w342"),
            BackdropUrl = Image(e, "backdrop_path", "w780"), TmdbRating = rating, TmdbVotes = votes
        };
    }

    private static int Int(JsonElement e, string field) => e.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
    private static bool IsTrue(JsonElement e, string field) => e.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.True;
    private static IEnumerable<JsonElement> JsonArray(JsonElement e, string field) => e.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();
    private static string? Image(JsonElement e, string field, string size)
    {
        var path = GetString(e, field);
        return path != null && Regex.IsMatch(path, @"^/[A-Za-z0-9._-]+$") ? "https://image.tmdb.org/t/p/" + size + path : null;
    }
}
