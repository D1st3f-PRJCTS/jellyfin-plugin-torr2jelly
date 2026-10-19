using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Torr2Jelly.Configuration;
using Jellyfin.Plugin.Torr2Jelly.Search;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torr2Jelly.Engine;

/// <summary>Outcome of <see cref="TorrentLinkResolver.ResolveAsync"/>.</summary>
public sealed record TorrentLinkResolution(bool Success, string Link, string? Error)
{
    public static TorrentLinkResolution Ok(string link) => new(true, link, null);

    public static TorrentLinkResolution Fail(string error) => new(false, string.Empty, error);
}

/// <summary>
/// Validates and prepares the link a client wants TorrServer to add. Only <c>magnet:</c> links, <c>http(s)://</c>
/// links and bare 40/64-character info-hashes are accepted (a bare hash becomes a magnet link). For http(s)
/// links the Prowlarr / Torznab API key is re-attached on the server (search results never carry it), and a
/// link that answers with a redirect to a magnet link (Prowlarr/Jackett do that for magnet-only indexers) is
/// replaced by that magnet link, because TorrServer cannot follow such a redirect.
/// </summary>
public sealed class TorrentLinkResolver
{
    /// <summary>Name of the HttpClient registered without automatic redirects (see PluginServiceRegistrator).</summary>
    public const string HttpClientName = "Torr2JellyLinkResolver";

    private const int MaxLinkLength = 16 * 1024;

    private readonly ILogger<TorrentLinkResolver> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Func<PluginConfiguration> _configAccessor;

    public TorrentLinkResolver(ILogger<TorrentLinkResolver> logger, IHttpClientFactory httpClientFactory)
        : this(logger, httpClientFactory, () => Plugin.Instance?.Configuration ?? new PluginConfiguration())
    {
    }

    internal TorrentLinkResolver(
        ILogger<TorrentLinkResolver> logger,
        IHttpClientFactory httpClientFactory,
        Func<PluginConfiguration> configAccessor)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _configAccessor = configAccessor;
    }

    /// <summary>
    /// Pure validation, no I/O. On success <paramref name="normalized"/> is the trimmed magnet / http(s) link
    /// (a bare hash is converted to a magnet link) and <paramref name="isHttp"/> says which kind it is.
    /// </summary>
    public static bool TryNormalize(string? link, out string normalized, out bool isHttp, out string? error)
    {
        normalized = string.Empty;
        isHttp = false;
        error = null;

        if (string.IsNullOrWhiteSpace(link))
        {
            error = "Torrent link or magnet is required.";
            return false;
        }

        var value = link.Trim();
        if (value.Length > MaxLinkLength)
        {
            error = "The torrent link is too long.";
            return false;
        }

        foreach (var ch in value)
        {
            if (ch < 0x20 || ch == 0x7F)
            {
                error = "The torrent link contains control characters.";
                return false;
            }
        }

        if (value.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            if (!HasInfoHash(value))
            {
                error = "The magnet link does not contain an info-hash (xt=urn:btih:...).";
                return false;
            }

            normalized = value;
            return true;
        }

        if (TorrentHash.IsValid(value))
        {
            var hash = TorrentHash.Normalize(value);

            // A 64-character hash is a BitTorrent v2 info-hash: its magnet form is a multihash (0x12 0x20 prefix).
            normalized = hash.Length == 64
                ? $"magnet:?xt=urn:btmh:1220{hash}"
                : $"magnet:?xt=urn:btih:{hash}";
            return true;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
            !string.IsNullOrEmpty(uri.Host))
        {
            normalized = value;
            isHttp = true;
            return true;
        }

        error = "Unsupported torrent link: use a magnet link, an http(s) link or a 40/64-character info-hash.";
        return false;
    }

    /// <summary>Returns <paramref name="url"/> when it is an absolute http(s) URL, otherwise <c>null</c>.</summary>
    public static string? SanitizePosterUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var value = url.Trim();
        if (value.Length > 4096 ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        return value;
    }

    public async Task<TorrentLinkResolution> ResolveAsync(string? link, CancellationToken cancellationToken = default)
    {
        if (!TryNormalize(link, out var normalized, out var isHttp, out var error))
        {
            return TorrentLinkResolution.Fail(error ?? "Invalid torrent link.");
        }

        if (!isHttp)
        {
            return TorrentLinkResolution.Ok(normalized);
        }

        // Search results are API-key-free; the key is added here, and only for the configured indexers' own hosts.
        var config = _configAccessor();
        var withKey = ProwlarrLinks.RestoreApiKey(normalized, config.ProwlarrUrl, config.ProwlarrApiKey);
        withKey = ProwlarrLinks.RestoreApiKey(withKey, config.TorznabUrl, config.TorznabApiKey);

        var magnet = await TryFollowToMagnetAsync(withKey, cancellationToken);
        return TorrentLinkResolution.Ok(magnet ?? withKey);
    }

    // Asks the link once, without following redirects. A 3xx whose Location is a magnet link yields that magnet.
    // Anything else (a .torrent download, an error, a timeout) leaves the decision to TorrServer.
    private async Task<string?> TryFollowToMagnetAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            var status = (int)response.StatusCode;
            if (status < 300 || status >= 400)
            {
                return null;
            }

            var location = ReadLocation(response);
            if (location != null &&
                location.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) &&
                TryNormalize(location, out var magnet, out var magnetIsHttp, out _) &&
                !magnetIsHttp)
            {
                _logger.LogDebug("Link {Url} redirects to a magnet link", ProwlarrLinks.RedactForLog(url));
                return magnet;
            }

            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Only the exception type is logged: messages of failed requests can carry the request URL (and its API key).
            _logger.LogWarning(
                "Could not check {Url} for a magnet redirect ({Error}); passing the link to TorrServer as it is",
                ProwlarrLinks.RedactForLog(url),
                ex.GetType().Name);
            return null;
        }
    }

    private static string? ReadLocation(HttpResponseMessage response)
    {
        var uri = response.Headers.Location;
        if (uri != null)
        {
            return uri.OriginalString;
        }

        // A magnet URI can fail the strict Location parser; the raw header value is still available.
        if (response.Headers.TryGetValues("Location", out var values))
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
        }

        return null;
    }

    private static bool HasInfoHash(string magnet)
    {
        if (ContainsInfoHash(magnet))
        {
            return true;
        }

        // Some clients percent-encode the colons: xt=urn%3Abtih%3A...
        try
        {
            return ContainsInfoHash(Uri.UnescapeDataString(magnet));
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private static bool ContainsInfoHash(string text)
    {
        return text.Contains("xt=urn:btih:", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("xt=urn:btmh:", StringComparison.OrdinalIgnoreCase);
    }
}
