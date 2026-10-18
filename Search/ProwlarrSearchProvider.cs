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

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("seeders")]
    public int Seeders { get; set; }

    [JsonPropertyName("leechers")]
    public int Leechers { get; set; }

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

    [JsonPropertyName("indexerFlags")]
    public List<string>? IndexerFlags { get; set; }

    [JsonPropertyName("downloadVolumeFactor")]
    public double? DownloadVolumeFactor { get; set; }

    [JsonPropertyName("uploadVolumeFactor")]
    public double? UploadVolumeFactor { get; set; }

    [JsonPropertyName("categories")]
    public List<ProwlarrCategoryItem>? Categories { get; set; }
}

public class ProwlarrSearchProvider : ITorrentSearchProvider
{
    public string Name => "Prowlarr";

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
        return (config.ProwlarrUrl.TrimEnd('/'), config.ProwlarrApiKey.Trim());
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
            using var res = await _httpClient.SendAsync(req, cancellationToken);
            res.EnsureSuccessStatusCode();

            var rawItems = await res.Content.ReadFromJsonAsync<List<ProwlarrRawItem>>(cancellationToken: cancellationToken);
            if (rawItems == null)
            {
                return new List<TorrentSearchResult>();
            }

            var results = new List<TorrentSearchResult>();
            foreach (var item in rawItems)
            {
                var link = item.MagnetUrl ?? item.DownloadUrl ?? item.InfoUrl;
                if (string.IsNullOrWhiteSpace(link) || string.IsNullOrWhiteSpace(item.Title))
                {
                    continue;
                }

                var flags = item.IndexerFlags ?? new List<string>();
                var tags = new List<string>();

                foreach (var f in flags)
                {
                    if (!string.IsNullOrWhiteSpace(f) && !tags.Contains(f, StringComparer.OrdinalIgnoreCase))
                    {
                        tags.Add(f.Trim());
                    }
                }

                var isFreeleech = flags.Any(f => f.Equals("freeleech", StringComparison.OrdinalIgnoreCase)) ||
                                  (item.DownloadVolumeFactor.HasValue && item.DownloadVolumeFactor.Value == 0);
                var isHalfleech = flags.Any(f => f.Equals("halfleech", StringComparison.OrdinalIgnoreCase)) ||
                                  (item.DownloadVolumeFactor.HasValue && item.DownloadVolumeFactor.Value == 0.5);
                var isBronze = (item.DownloadVolumeFactor.HasValue && item.DownloadVolumeFactor.Value == 0.75) ||
                               flags.Any(f => f.Equals("bronze", StringComparison.OrdinalIgnoreCase));

                if (isFreeleech && !tags.Contains("freeleech", StringComparer.OrdinalIgnoreCase))
                {
                    tags.Add("freeleech");
                }
                else if (isHalfleech && !tags.Contains("halfleech", StringComparer.OrdinalIgnoreCase))
                {
                    tags.Add("halfleech");
                }
                else if (isBronze && !tags.Contains("bronze", StringComparer.OrdinalIgnoreCase))
                {
                    tags.Add("bronze");
                }

                if (item.UploadVolumeFactor.HasValue && item.UploadVolumeFactor.Value > 1 && !tags.Contains("doubleupload", StringComparer.OrdinalIgnoreCase))
                {
                    tags.Add("doubleupload");
                }

                var rawTitle = item.Title ?? string.Empty;
                if ((rawTitle.Contains("[Авторськ", StringComparison.OrdinalIgnoreCase) ||
                     rawTitle.Contains("Авторський", StringComparison.OrdinalIgnoreCase) ||
                     rawTitle.Contains("Власна роздача", StringComparison.OrdinalIgnoreCase)) &&
                    !tags.Contains("internal", StringComparer.OrdinalIgnoreCase))
                {
                    tags.Add("internal");
                }

                if ((rawTitle.Contains("[Exclusive]", StringComparison.OrdinalIgnoreCase) ||
                     rawTitle.Contains("Ексклюзив", StringComparison.OrdinalIgnoreCase)) &&
                    !tags.Contains("exclusive", StringComparer.OrdinalIgnoreCase))
                {
                    tags.Add("exclusive");
                }

                var searchResult = new TorrentSearchResult
                {
                    Id = item.Guid ?? item.InfoHash ?? Guid.NewGuid().ToString("N"),
                    Title = (item.Title ?? string.Empty).Trim(),
                    Size = item.Size,
                    SizeFormatted = TorrentSearchResult.FormatBytes(item.Size),
                    Seeders = item.Seeders,
                    Peers = item.Leechers,
                    Indexer = item.Indexer ?? "Unknown",
                    DownloadUrl = item.DownloadUrl ?? link,
                    MagnetUrl = item.MagnetUrl ?? (link.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ? link : string.Empty),
                    InfoHash = item.InfoHash ?? string.Empty,
                    IsGold = isFreeleech,
                    IsFreeleech = isFreeleech || isHalfleech,
                    Tags = tags,
                    PublishDate = item.PublishDate,
                    Categories = item.Categories?.Select(c => c.Id).ToList() ?? new List<int>()
                };

                results.Add(searchResult);
            }

            // Priority sorting: Tagged/Gold -> Regular, then by seeders desc
            return results
                .OrderByDescending(r => (r.Tags != null && r.Tags.Count > 0) ? (r.IsGold ? 3 : 2) : 0)
                .ThenByDescending(r => r.Seeders)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search Prowlarr at {Url}", fullUrl);
            return new List<TorrentSearchResult>();
        }
    }
}
