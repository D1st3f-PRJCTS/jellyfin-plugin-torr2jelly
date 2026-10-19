using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Jellyfin.Plugin.Torr2Jelly.Configuration;
using Jellyfin.Plugin.Torr2Jelly.Search.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torr2Jelly.Search;

public class TorznabSearchProvider : ITorrentSearchProvider
{
    public string Name => "Torznab";

    private const string FallbackIndexerName = "Torznab";

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
        return ((config.TorznabUrl ?? string.Empty).TrimEnd('/'), (config.TorznabApiKey ?? string.Empty).Trim());
    }

    /// <summary>
    /// Appends query parameters to the configured base URL. The base may already contain a query
    /// (e.g. <c>.../api?apikey=...</c>), in which case the new parameters are joined with '&amp;'.
    /// The API key is added only when the base URL does not already carry one.
    /// </summary>
    private static string BuildUrl(string baseUrl, string apiKey, string function, string? query = null, IEnumerable<int>? categories = null)
    {
        var url = baseUrl;
        var first = true;

        void Append(string param)
        {
            if (first)
            {
                if (url.IndexOf('?') < 0)
                {
                    url += "?";
                }
                else if (!url.EndsWith("?", StringComparison.Ordinal) && !url.EndsWith("&", StringComparison.Ordinal))
                {
                    url += "&";
                }

                first = false;
            }
            else
            {
                url += "&";
            }

            url += param;
        }

        Append($"t={function}");
        if (query != null)
        {
            Append($"q={Uri.EscapeDataString(query)}");
        }

        if (!string.IsNullOrWhiteSpace(apiKey) && !ProwlarrLinks.HasApiKey(baseUrl))
        {
            Append($"apikey={Uri.EscapeDataString(apiKey)}");
        }

        var cats = categories?.ToList();
        if (cats != null && cats.Count > 0)
        {
            Append($"cat={string.Join(",", cats)}");
        }

        return url;
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
            var url = BuildUrl(baseUrl, apiKey, "caps");
            using var res = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return res.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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

        var url = BuildUrl(baseUrl, apiKey, "search", query, categories);

        try
        {
            using var res = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            res.EnsureSuccessStatusCode();
            var xmlString = await res.Content.ReadAsStringAsync(cancellationToken);

            // DTDs are ignored (not resolved/expanded); the feed is untrusted input.
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                MaxCharactersInDocument = 50_000_000
            };

            XDocument doc;
            using (var sr = new StringReader(xmlString))
            using (var reader = XmlReader.Create(sr, settings))
            {
                doc = XDocument.Load(reader);
            }

            if (string.Equals(doc.Root?.Name.LocalName, "error", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Torznab returned an error: {Code} {Description}",
                    doc.Root?.Attribute("code")?.Value,
                    doc.Root?.Attribute("description")?.Value);
                return new List<TorrentSearchResult>();
            }

            var results = new List<TorrentSearchResult>();
            var skippedBad = 0;
            foreach (var item in doc.Descendants("item"))
            {
                try
                {
                    var mapped = MapItem(item);
                    if (mapped != null)
                    {
                        results.Add(mapped);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    skippedBad++;
                    _logger.LogDebug(ex, "Skipping a malformed Torznab item");
                }
            }

            if (skippedBad > 0)
            {
                _logger.LogWarning("Torznab search: skipped {Count} malformed item(s)", skippedBad);
            }

            return results
                .OrderByDescending(r => (r.Tags != null && r.Tags.Count > 0) ? (r.IsGold ? 3 : 2) : 0)
                .ThenByDescending(r => r.Seeders)
                .ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Torznab search timed out at {Url}", ProwlarrLinks.RedactForLog(url));
            return new List<TorrentSearchResult>();
        }
        catch (Exception ex)
        {
            // The request URL carries the API key; never log it unredacted.
            _logger.LogError(ex, "Failed to search Torznab at {Url}", ProwlarrLinks.RedactForLog(url));
            return new List<TorrentSearchResult>();
        }
    }

    private static bool IsMagnet(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.TrimStart().StartsWith("magnet:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Maps one &lt;item&gt;. Returns null when it has no title or nothing that could be added.</summary>
    private static TorrentSearchResult? MapItem(XElement item)
    {
        var title = item.Element("title")?.Value?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            return null;
        }

        var link = item.Element("link")?.Value?.Trim();
        var enclosure = item.Element("enclosure");
        var enclosureUrl = enclosure?.Attribute("url")?.Value?.Trim();
        var enclosureLength = enclosure?.Attribute("length")?.Value;

        long size = 0;
        if (SearchParse.TryLong(enclosureLength, out var enclosureSize) && enclosureSize > 0)
        {
            size = enclosureSize;
        }
        else if (SearchParse.TryLong(item.Element("size")?.Value, out var elementSize) && elementSize > 0)
        {
            size = elementSize;
        }

        var seeders = 0;
        var peers = 0;
        var leechers = -1;
        var infoHashRaw = string.Empty;
        var magnetAttr = string.Empty;
        string? indexerName = null;
        string? posterUrl = null;
        string? imdbId = null;
        int? tmdbId = null;
        double? downloadFactor = null;
        double? uploadFactor = null;
        var tags = new TagCollector();

        foreach (var attr in item.Elements(TorznabNs + "attr"))
        {
            var attrName = attr.Attribute("name")?.Value?.Trim().ToLowerInvariant();
            var attrVal = attr.Attribute("value")?.Value;

            switch (attrName)
            {
                case "seeders":
                    if (SearchParse.TryInt(attrVal, out var s))
                    {
                        seeders = Math.Max(0, s);
                    }

                    break;
                case "peers":
                    if (SearchParse.TryInt(attrVal, out var p))
                    {
                        peers = Math.Max(0, p);
                    }

                    break;
                case "leechers":
                    if (SearchParse.TryInt(attrVal, out var l))
                    {
                        leechers = Math.Max(0, l);
                    }

                    break;
                case "size":
                    // Only a fallback: the enclosure length / <size> element win when present.
                    if (size == 0 && SearchParse.TryLong(attrVal, out var attrSize) && attrSize > 0)
                    {
                        size = attrSize;
                    }

                    break;
                case "infohash":
                    infoHashRaw = attrVal ?? string.Empty;
                    break;
                case "magneturl":
                    if (IsMagnet(attrVal))
                    {
                        magnetAttr = attrVal!.Trim();
                    }

                    break;
                case "tag":
                    if (!string.IsNullOrWhiteSpace(attrVal))
                    {
                        foreach (var t in attrVal.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            tags.AddRaw(t);
                        }
                    }

                    break;
                case "downloadvolumefactor":
                    if (SearchParse.TryDouble(attrVal, out var factor))
                    {
                        downloadFactor = factor;
                    }

                    break;
                case "uploadvolumefactor":
                    if (SearchParse.TryDouble(attrVal, out var upFactor))
                    {
                        uploadFactor = upFactor;
                    }

                    break;
                case "imdb":
                case "imdbid":
                    if (!string.IsNullOrWhiteSpace(attrVal))
                    {
                        var v = attrVal.Trim();
                        imdbId = v.StartsWith("tt", StringComparison.OrdinalIgnoreCase)
                            ? v
                            : (SearchParse.TryLong(v, out var inum) && inum > 0 ? $"tt{inum:D7}" : v);
                    }

                    break;
                case "tmdb":
                case "tmdbid":
                    if (SearchParse.TryInt(attrVal, out var tid) && tid > 0)
                    {
                        tmdbId = tid;
                    }

                    break;
                case "poster":
                case "banner":
                    if (!string.IsNullOrWhiteSpace(attrVal))
                    {
                        posterUrl = attrVal.Trim();
                    }

                    break;
                case "jackettindexer":
                case "prowlarrindexer":
                    if (string.IsNullOrWhiteSpace(indexerName) && !string.IsNullOrWhiteSpace(attrVal))
                    {
                        indexerName = attrVal.Trim();
                    }

                    break;
            }
        }

        // Jackett / Prowlarr name the source tracker in a dedicated element: <jackettindexer id="x">Name</jackettindexer>.
        if (string.IsNullOrWhiteSpace(indexerName))
        {
            var indexerElement = item.Elements().FirstOrDefault(e =>
                e.Name.LocalName.Equals("jackettindexer", StringComparison.OrdinalIgnoreCase) ||
                e.Name.LocalName.Equals("prowlarrindexer", StringComparison.OrdinalIgnoreCase));
            if (indexerElement != null)
            {
                indexerName = !string.IsNullOrWhiteSpace(indexerElement.Value)
                    ? indexerElement.Value.Trim()
                    : indexerElement.Attribute("id")?.Value?.Trim();
            }
        }

        // Discount state is resolved only after every attribute has been read, so attribute order is irrelevant.
        tags.AddDownloadFactor(downloadFactor);
        tags.AddUploadFactor(uploadFactor);
        tags.AddTitle(title);

        // Magnet: explicit attribute, then a magnet used as <link> / <enclosure>, then synthesised from the info-hash.
        var magnet = magnetAttr;
        if (magnet.Length == 0 && IsMagnet(link))
        {
            magnet = link!;
        }

        if (magnet.Length == 0 && IsMagnet(enclosureUrl))
        {
            magnet = enclosureUrl!;
        }

        var infoHash = InfoHashHelper.Normalize(infoHashRaw);
        if (infoHash.Length == 0 && magnet.Length > 0)
        {
            infoHash = InfoHashHelper.FromMagnet(magnet);
        }

        // Download URL: never carries an apikey towards the browser.
        var httpDownload = string.Empty;
        foreach (var candidate in new[] { enclosureUrl, link })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && !IsMagnet(candidate))
            {
                httpDownload = ProwlarrLinks.StripApiKey(candidate);
                break;
            }
        }

        // A magnet built from the info-hash alone has no trackers, which a private tracker's torrent never
        // resolves. It is only a last resort: when the indexer gave an http(s) .torrent link, that link wins.
        if (magnet.Length == 0 && infoHash.Length > 0 && httpDownload.Length == 0)
        {
            magnet = InfoHashHelper.BuildMagnet(infoHash, title);
        }

        if (magnet.Length == 0 && httpDownload.Length == 0)
        {
            return null;
        }

        var guid = item.Element("guid")?.Value?.Trim();
        var id = infoHash.Length > 0
            ? infoHash
            : (!string.IsNullOrWhiteSpace(guid) && !IsMagnet(guid) ? ProwlarrLinks.StripApiKey(guid) : Guid.NewGuid().ToString("N"));

        return new TorrentSearchResult
        {
            Id = id,
            Title = title,
            Size = size,
            SizeFormatted = TorrentSearchResult.FormatBytes(size),
            Seeders = seeders,
            Peers = leechers >= 0 ? leechers : peers,
            Indexer = string.IsNullOrWhiteSpace(indexerName) ? FallbackIndexerName : indexerName.Trim(),
            DownloadUrl = httpDownload.Length > 0 ? httpDownload : magnet,
            MagnetUrl = magnet,
            InfoHash = infoHash,
            IsGold = tags.IsFreeleech,
            IsFreeleech = tags.IsFreeleech,
            Tags = tags.ToList(),
            PosterUrl = posterUrl != null ? ProwlarrLinks.StripApiKey(posterUrl) : null,
            ImdbId = imdbId,
            TmdbId = tmdbId
        };
    }
}
