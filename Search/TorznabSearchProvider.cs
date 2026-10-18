using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Plugin.Torr2Jelly.Configuration;
using Jellyfin.Plugin.Torr2Jelly.Search.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torr2Jelly.Search;

public class TorznabSearchProvider : ITorrentSearchProvider
{
    public string Name => "Torznab";

    private readonly ILogger<TorznabSearchProvider> _logger;
    private readonly HttpClient _httpClient;
    private static readonly XNamespace TorznabNs = "http://torznab.com/schemas/2015/feed";

    public TorznabSearchProvider(
        ILogger<TorznabSearchProvider> logger,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("TorznabClient");
        _httpClient.Timeout = TimeSpan.FromSeconds(20);
    }

    private (string Url, string ApiKey) GetConfig()
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        return (config.TorznabUrl.TrimEnd('/'), config.TorznabApiKey.Trim());
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var (baseUrl, apiKey) = GetConfig();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return false;
        }

        try
        {
            var url = $"{baseUrl}?t=caps";
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                url += $"&apikey={Uri.EscapeDataString(apiKey)}";
            }

            var res = await _httpClient.GetAsync(url, cancellationToken);
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Torznab connection test failed");
            return false;
        }
    }

    public async Task<List<TorrentSearchResult>> SearchAsync(
        string query,
        IEnumerable<int>? categories = null,
        CancellationToken cancellationToken = default)
    {
        var (baseUrl, apiKey) = GetConfig();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(query))
        {
            return new List<TorrentSearchResult>();
        }

        var url = $"{baseUrl}?t=search&q={Uri.EscapeDataString(query)}";
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            url += $"&apikey={Uri.EscapeDataString(apiKey)}";
        }

        if (categories != null && categories.Any())
        {
            url += $"&cat={string.Join(",", categories)}";
        }

        try
        {
            var xmlString = await _httpClient.GetStringAsync(url, cancellationToken);
            var doc = XDocument.Parse(xmlString);
            var items = doc.Descendants("item");

            var results = new List<TorrentSearchResult>();
            foreach (var item in items)
            {
                var title = item.Element("title")?.Value;
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var link = item.Element("link")?.Value;
                var enclosure = item.Element("enclosure");
                var enclosureUrl = enclosure?.Attribute("url")?.Value;
                var enclosureLength = enclosure?.Attribute("length")?.Value;

                long size = 0;
                if (!string.IsNullOrWhiteSpace(enclosureLength) && long.TryParse(enclosureLength, out var sz))
                {
                    size = sz;
                }
                else if (long.TryParse(item.Element("size")?.Value, out var sz2))
                {
                    size = sz2;
                }

                int seeders = 0;
                int peers = 0;
                string infoHash = string.Empty;
                bool isGold = false;
                bool isHalfleech = false;
                var tags = new List<string>();

                foreach (var attr in item.Elements(TorznabNs + "attr"))
                {
                    var attrName = attr.Attribute("name")?.Value?.ToLowerInvariant();
                    var attrVal = attr.Attribute("value")?.Value;

                    if (attrName == "seeders" && int.TryParse(attrVal, out var s))
                    {
                        seeders = s;
                    }
                    else if (attrName == "peers" && int.TryParse(attrVal, out var p))
                    {
                        peers = p;
                    }
                    else if (attrName == "infohash")
                    {
                        infoHash = attrVal ?? string.Empty;
                    }
                    else if (attrName == "tag" && !string.IsNullOrWhiteSpace(attrVal))
                    {
                        if (!tags.Contains(attrVal, StringComparer.OrdinalIgnoreCase))
                        {
                            tags.Add(attrVal.Trim());
                        }
                    }
                    else if (attrName == "downloadvolumefactor" && double.TryParse(attrVal, out var factor))
                    {
                        if (factor == 0)
                        {
                            isGold = true;
                            if (!tags.Contains("freeleech", StringComparer.OrdinalIgnoreCase))
                                tags.Add("freeleech");
                        }
                        else if (factor == 0.5)
                        {
                            isHalfleech = true;
                            if (!tags.Contains("halfleech", StringComparer.OrdinalIgnoreCase))
                                tags.Add("halfleech");
                        }
                        else if (factor == 0.75)
                        {
                            if (!tags.Contains("bronze", StringComparer.OrdinalIgnoreCase))
                                tags.Add("bronze");
                        }
                    }
                    else if (attrName == "uploadvolumefactor" && double.TryParse(attrVal, out var upFactor) && upFactor > 1)
                    {
                        if (!tags.Contains("doubleupload", StringComparer.OrdinalIgnoreCase))
                            tags.Add("doubleupload");
                    }
                }

                if ((title.Contains("[Авторськ", StringComparison.OrdinalIgnoreCase) ||
                     title.Contains("Авторський", StringComparison.OrdinalIgnoreCase) ||
                     title.Contains("Власна роздача", StringComparison.OrdinalIgnoreCase)) &&
                    !tags.Contains("internal", StringComparer.OrdinalIgnoreCase))
                {
                    tags.Add("internal");
                }

                if ((title.Contains("[Exclusive]", StringComparison.OrdinalIgnoreCase) ||
                     title.Contains("Ексклюзив", StringComparison.OrdinalIgnoreCase)) &&
                    !tags.Contains("exclusive", StringComparer.OrdinalIgnoreCase))
                {
                    tags.Add("exclusive");
                }

                var downloadUrl = enclosureUrl ?? link ?? string.Empty;
                var magnetUrl = link != null && link.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ? link : string.Empty;

                results.Add(new TorrentSearchResult
                {
                    Id = infoHash != string.Empty ? infoHash : Guid.NewGuid().ToString("N"),
                    Title = title.Trim(),
                    Size = size,
                    SizeFormatted = TorrentSearchResult.FormatBytes(size),
                    Seeders = seeders,
                    Peers = peers,
                    Indexer = "Torznab",
                    DownloadUrl = downloadUrl,
                    MagnetUrl = magnetUrl,
                    InfoHash = infoHash,
                    IsGold = isGold,
                    IsFreeleech = isGold || isHalfleech,
                    Tags = tags
                });
            }

            return results
                .OrderByDescending(r => (r.Tags != null && r.Tags.Count > 0) ? (r.IsGold ? 3 : 2) : 0)
                .ThenByDescending(r => r.Seeders)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search Torznab at {Url}", url);
            return new List<TorrentSearchResult>();
        }
    }
}
