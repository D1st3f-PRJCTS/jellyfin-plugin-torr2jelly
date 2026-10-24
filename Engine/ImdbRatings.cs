using System.Globalization;
using System.IO.Compression;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torr2Jelly.Engine;

public sealed record ImdbRating(double Score, long Votes);

/// <summary>Optional daily IMDb ratings, kept compressed on disk. No full-dataset RAM dictionary or extra API key.</summary>
public sealed class ImdbRatings(IApplicationPaths paths, IHttpClientFactory factory, ILogger<ImdbRatings> logger) : BackgroundService
{
    private const long MaxBytes = 64L * 1024 * 1024;
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly Dictionary<string, (DateTime Version, ImdbRating? Rating)> _cache = new();
    private string CacheFile => Path.Combine(paths.DataPath, "torr2jelly", "imdb-ratings.tsv.gz");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                if (TmdbClient.HasStoredKey())
                {
                    try { await RefreshAsync(stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception ex) { logger.LogWarning("IMDb ratings refresh failed ({Type}); retaining the previous cache", ex.GetType().Name); }
                }
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    internal async Task RefreshAsync(CancellationToken ct)
    {
        var path = CacheFile;
        if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromHours(24)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        using var client = factory.CreateClient("ImdbRatings");
        using var response = await client.GetAsync("https://datasets.imdbws.com/title.ratings.tsv.gz", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxBytes) throw new InvalidDataException();
        var temporary = path + ".new";
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
            {
                var buffer = new byte[65536];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    if ((total += read) > MaxBytes) throw new InvalidDataException();
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                }
            }
            // A partial/corrupt response must not replace a good cache. Validate the entire gzip stream too.
            await using (var file = File.OpenRead(temporary))
            await using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            using (var reader = new StreamReader(gzip))
            {
                if (await reader.ReadLineAsync(timeout.Token) != "tconst\taverageRating\tnumVotes") throw new InvalidDataException();
                var buffer = new char[65536];
                long total = 0;
                int read;
                while ((read = await reader.ReadAsync(buffer.AsMemory(), timeout.Token)) > 0)
                    if ((total += read) > 512L * 1024 * 1024) throw new InvalidDataException();
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<ImdbRating?> GetAsync(string? imdbId, CancellationToken ct = default)
    {
        if (imdbId == null || !System.Text.RegularExpressions.Regex.IsMatch(imdbId, "^tt[0-9]{1,10}$") || !File.Exists(CacheFile)) return null;
        await _readLock.WaitAsync(ct);
        try
        {
            var version = File.GetLastWriteTimeUtc(CacheFile);
            if (_cache.TryGetValue(imdbId, out var cached) && cached.Version == version) return cached.Rating;
            await using var file = new FileStream(CacheFile, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, true);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            ImdbRating? result = null;
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) != null)
            {
                if (!line.StartsWith(imdbId + '\t', StringComparison.Ordinal)) continue;
                var fields = line.Split('\t');
                if (fields.Length == 3 && double.TryParse(fields[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var score) && score is >= 0 and <= 10 && long.TryParse(fields[2], out var votes) && votes > 0)
                    result = new ImdbRating(score, votes);
                break;
            }
            if (_cache.Count >= 512) _cache.Clear();
            _cache[imdbId] = (version, result);
            return result;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { return null; }
        finally { _readLock.Release(); }
    }
}
