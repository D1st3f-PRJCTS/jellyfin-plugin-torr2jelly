using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Torr2Jelly.Configuration;
using Jellyfin.Plugin.Torr2Jelly.Search.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torr2Jelly.Search;

public class ProwlarrCategoryItem
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public class ProwlarrIndexerDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("enable")]
    public bool Enable { get; set; }
}

public class ProwlarrRawItem
{
    [JsonPropertyName("guid")]
    public string? Guid { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("indexer")]
    public string? Indexer { get; set; }

    // Seeders / leechers / size are absent or null for some indexers (public trackers, usenet).
    [JsonPropertyName("size")]
    public long? Size { get; set; }

    [JsonPropertyName("seeders")]
    public int? Seeders { get; set; }

    [JsonPropertyName("leechers")]
    public int? Leechers { get; set; }

    [JsonPropertyName("publishDate")]
    public DateTime? PublishDate { get; set; }

    [JsonPropertyName("downloadUrl")]
    public string? DownloadUrl { get; set; }

    [JsonPropertyName("magnetUrl")]
    public string? MagnetUrl { get; set; }

    [JsonPropertyName("infoUrl")]
    public string? InfoUrl { get; set; }

    [JsonPropertyName("infoHash")]
    public string? InfoHash { get; set; }

    /// <summary>"torrent" / "usenet". Kept as a raw JSON value so an unexpected shape cannot break deserialization.</summary>
    [JsonPropertyName("protocol")]
    public JsonElement? Protocol { get; set; }

    [JsonPropertyName("indexerFlags")]
    public List<string>? IndexerFlags { get; set; }

    [JsonPropertyName("downloadVolumeFactor")]
    public double? DownloadVolumeFactor { get; set; }

    [JsonPropertyName("uploadVolumeFactor")]
    public double? UploadVolumeFactor { get; set; }

    [JsonPropertyName("categories")]
    public List<ProwlarrCategoryItem>? Categories { get; set; }

    [JsonPropertyName("imdbId")]
    public long? ImdbId { get; set; }

    [JsonPropertyName("tmdbId")]
    public int? TmdbId { get; set; }

    [JsonPropertyName("tvdbId")]
    public int? TvdbId { get; set; }

    [JsonPropertyName("posterUrl")]
    public string? PosterUrl { get; set; }
}

public class ProwlarrSearchProvider : ITorrentSearchProvider
{
    public string Name => "Prowlarr";

    private static readonly JsonSerializerOptions ItemJsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly ILogger<ProwlarrSearchProvider> _logger;
    private readonly HttpClient _httpClient;

    public ProwlarrSearchProvider(
        ILogger<ProwlarrSearchProvider> logger,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("ProwlarrClient");
        _httpClient.Timeout = TimeSpan.FromSeconds(20);
    }

    private (string Url, string ApiKey) GetConfig()
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        return ((config.ProwlarrUrl ?? string.Empty).TrimEnd('/'), (config.ProwlarrApiKey ?? string.Empty).Trim());
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var (baseUrl, apiKey) = GetConfig();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            return false;
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/system/status");
            req.Headers.Add("X-Api-Key", apiKey);
            using var res = await _httpClient.SendAsync(req, cancellationToken);
            return res.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Prowlarr connection test failed");
            return false;
        }
    }

    public async Task<List<ProwlarrIndexerDto>> GetIndexersAsync(CancellationToken cancellationToken = default)
    {
        var (baseUrl, apiKey) = GetConfig();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            return new List<ProwlarrIndexerDto>();
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/indexer");
            req.Headers.Add("X-Api-Key", apiKey);
            using var res = await _httpClient.SendAsync(req, cancellationToken);
            res.EnsureSuccessStatusCode();

            var indexers = await res.Content.ReadFromJsonAsync<List<ProwlarrIndexerDto>>(cancellationToken: cancellationToken);
            return indexers ?? new List<ProwlarrIndexerDto>();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch indexers from Prowlarr");
            return new List<ProwlarrIndexerDto>();
        }
    }

    public async Task<List<TorrentSearchResult>> SearchAsync(
        string query,
        IEnumerable<int>? categories = null,
        CancellationToken cancellationToken = default)
    {
        var (baseUrl, apiKey) = GetConfig();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(query))
        {
            return new List<TorrentSearchResult>();
        }

        var urlBuilder = new List<string>
        {
            $"{baseUrl}/api/v1/search?query={Uri.EscapeDataString(query)}&type=search&limit=60"
        };

        if (categories != null)
        {
            foreach (var cat in categories)
            {
                urlBuilder.Add($"categories={cat}");
            }
        }

        var fullUrl = urlBuilder[0];
        if (urlBuilder.Count > 1)
        {
            fullUrl += "&" + string.Join("&", urlBuilder.Skip(1));
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, fullUrl);
            req.Headers.Add("X-Api-Key", apiKey);
            using var res = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            res.EnsureSuccessStatusCode();

            await using var stream = await res.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("Prowlarr search returned unexpected JSON ({Kind}), ignoring", doc.RootElement.ValueKind);
                return new List<TorrentSearchResult>();
            }

            var results = new List<TorrentSearchResult>();
            var skippedBad = 0;
            var skippedUsenet = 0;
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                // One malformed item must cost only that item, never the whole response.
                try
                {
                    var item = element.Deserialize<ProwlarrRawItem>(ItemJsonOptions);
                    if (item == null)
                    {
                        continue;
                    }

                    if (IsUsenet(item.Protocol))
                    {
                        skippedUsenet++;
                        continue;
                    }

                    var mapped = MapItem(item);
                    if (mapped != null)
                    {
                        results.Add(mapped);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    skippedBad++;
                    _logger.LogDebug(ex, "Skipping a malformed Prowlarr search item");
                }
            }

            if (skippedBad > 0)
            {
                _logger.LogWarning("Prowlarr search: skipped {Count} malformed item(s)", skippedBad);
            }

            if (skippedUsenet > 0)
            {
                _logger.LogDebug("Prowlarr search: skipped {Count} usenet result(s)", skippedUsenet);
            }

            return results
                .OrderByDescending(r => r.Seeders)
                .ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Prowlarr search timed out at {Url}", fullUrl);
            return new List<TorrentSearchResult>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search Prowlarr at {Url}", fullUrl);
            return new List<TorrentSearchResult>();
        }
    }

    private static bool IsUsenet(JsonElement? protocol) =>
        protocol is { ValueKind: JsonValueKind.String } p &&
        string.Equals(p.GetString()?.Trim(), "usenet", StringComparison.OrdinalIgnoreCase);

    private static bool IsMagnet(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.TrimStart().StartsWith("magnet:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Maps one Prowlarr result. Returns null when it cannot be added (no title, or no link and no info-hash).</summary>
    private static TorrentSearchResult? MapItem(ProwlarrRawItem item)
    {
        var title = item.Title?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            return null;
        }

        // Magnet: the dedicated field, then a magnet used as guid / download url.
        var magnet = string.Empty;
        foreach (var candidate in new[] { item.MagnetUrl, item.Guid, item.DownloadUrl })
        {
            if (IsMagnet(candidate))
            {
                magnet = candidate!.Trim();
                break;
            }
        }

        var infoHash = InfoHashHelper.Normalize(item.InfoHash);
        if (infoHash.Length == 0 && magnet.Length > 0)
        {
            infoHash = InfoHashHelper.FromMagnet(magnet);
        }

        // The browser never gets the Prowlarr API key; the server re-attaches it when adding the torrent.
        var httpDownload = !string.IsNullOrWhiteSpace(item.DownloadUrl) && !IsMagnet(item.DownloadUrl)
            ? ProwlarrLinks.StripApiKey(item.DownloadUrl.Trim())
            : string.Empty;

        // A magnet built from the info-hash alone has no trackers, which a private tracker's torrent never
        // resolves. It is only a last resort: when the indexer gave an http(s) .torrent link, that link wins.
        if (magnet.Length == 0 && infoHash.Length > 0 && httpDownload.Length == 0)
        {
            magnet = InfoHashHelper.BuildMagnet(infoHash, title);
        }

        if (magnet.Length == 0 && httpDownload.Length == 0)
        {
            // Nothing that could be added to TorrServer (an info page URL alone is not a torrent).
            return null;
        }

        var tags = new TagCollector();
        if (item.IndexerFlags != null)
        {
            foreach (var flag in item.IndexerFlags)
            {
                tags.AddRaw(flag);
            }
        }

        tags.AddDownloadFactor(item.DownloadVolumeFactor);
        tags.AddUploadFactor(item.UploadVolumeFactor);
        tags.AddTitle(title);

        var size = Math.Max(0, item.Size ?? 0);
        var guid = !string.IsNullOrWhiteSpace(item.Guid) && !IsMagnet(item.Guid)
            ? ProwlarrLinks.StripApiKey(item.Guid.Trim())
            : null;

        return new TorrentSearchResult
        {
            Id = guid ?? (infoHash.Length > 0 ? infoHash : Guid.NewGuid().ToString("N")),
            Title = title,
            Size = size,
            SizeFormatted = TorrentSearchResult.FormatBytes(size),
            Seeders = Math.Max(0, item.Seeders ?? 0),
            Peers = Math.Max(0, item.Leechers ?? 0),
            Indexer = string.IsNullOrWhiteSpace(item.Indexer) ? "Unknown" : item.Indexer.Trim(),
            DownloadUrl = httpDownload.Length > 0 ? httpDownload : magnet,
            MagnetUrl = magnet,
            InfoHash = infoHash,
            IsGold = tags.IsFreeleech,
            IsFreeleech = tags.IsStrictFreeleech,
            DownloadVolumeFactor = tags.DownloadVolumeFactor,
            Tags = tags.ToList(),
            PublishDate = item.PublishDate,
            Categories = item.Categories?.Where(c => c != null).Select(c => c.Id).ToList() ?? new List<int>(),
            PosterUrl = !string.IsNullOrWhiteSpace(item.PosterUrl) ? ProwlarrLinks.StripApiKey(item.PosterUrl.Trim()) : null,
            ImdbId = item.ImdbId.HasValue && item.ImdbId.Value > 0 ? $"tt{item.ImdbId.Value:D7}" : null,
            TmdbId = item.TmdbId.HasValue && item.TmdbId.Value > 0 ? item.TmdbId.Value : null
        };
    }
}
