using System;
using System.Collections.Generic;
using System.Text;

namespace Jellyfin.Plugin.Torr2Jelly.Search;

/// <summary>
/// Helpers for Prowlarr / Jackett download links. Prowlarr's <c>downloadUrl</c> embeds the instance API key
/// (<c>?apikey=...</c>) and Jackett's download links carry it as <c>?jackett_apikey=...</c>; that key must never
/// reach the browser, so search results are stripped and the key is re-attached on the server right before the
/// link is fetched.
/// </summary>
public static class ProwlarrLinks
{
    private const string ApiKeyParam = "apikey";
    private const string JackettApiKeyParam = "jackett_apikey";

    // Query parameters that carry an indexer-proxy API key (Prowlarr, Jackett).
    private static readonly string[] ApiKeyParams = { ApiKeyParam, JackettApiKeyParam };

    // Query parameters whose values are secrets and must not appear in logs.
    private static readonly string[] SecretParams = { "apikey", "jackett_apikey", "api_key", "passkey", "token", "key" };

    /// <summary>
    /// Removes the <c>apikey</c> and <c>jackett_apikey</c> query parameters (case-insensitive names) from
    /// <paramref name="url"/>. Everything else (encoding, other parameters, fragment) is preserved byte-for-byte.
    /// Magnet links and URLs without such a parameter are returned unchanged.
    /// </summary>
    public static string StripApiKey(string url)
    {
        if (string.IsNullOrEmpty(url) || url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        if (!TrySplit(url, out var prefix, out var query, out var fragment))
        {
            return url;
        }

        var kept = new List<string>();
        var removed = false;
        foreach (var part in query.Split('&'))
        {
            if (part.Length == 0)
            {
                continue;
            }

            if (IsApiKeyParam(part))
            {
                removed = true;
                continue;
            }

            kept.Add(part);
        }

        if (!removed)
        {
            return url;
        }

        return kept.Count > 0
            ? prefix + "?" + string.Join("&", kept) + fragment
            : prefix + fragment;
    }

    /// <summary>
    /// Re-adds the indexer API key to <paramref name="url"/>, but only when the URL points at the
    /// configured Prowlarr / Jackett instance (same scheme, host and port as <paramref name="prowlarrBaseUrl"/>)
    /// and carries neither an <c>apikey</c> nor a <c>jackett_apikey</c> parameter yet. Jackett's download path
    /// (<c>/dl/</c>) gets <c>jackett_apikey</c>, everything else <c>apikey</c>. In every other case the URL is
    /// returned unchanged, so the key can never be sent to a foreign host.
    /// </summary>
    public static string RestoreApiKey(string url, string? prowlarrBaseUrl, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            string.IsNullOrWhiteSpace(prowlarrBaseUrl) ||
            string.IsNullOrWhiteSpace(apiKey))
        {
            return url;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var target) || !IsHttp(target) ||
            !Uri.TryCreate(prowlarrBaseUrl.Trim(), UriKind.Absolute, out var baseUri) || !IsHttp(baseUri))
        {
            return url;
        }

        if (!string.Equals(target.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(target.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) ||
            target.Port != baseUri.Port)
        {
            return url;
        }

        if (HasApiKey(url))
        {
            return url;
        }

        var trimmed = url.Trim();
        var fragmentIdx = trimmed.IndexOf('#');
        var fragment = fragmentIdx >= 0 ? trimmed.Substring(fragmentIdx) : string.Empty;
        var head = fragmentIdx >= 0 ? trimmed.Substring(0, fragmentIdx) : trimmed;

        var sb = new StringBuilder(head);
        if (head.IndexOf('?') < 0)
        {
            sb.Append('?');
        }
        else if (!head.EndsWith("?", StringComparison.Ordinal) && !head.EndsWith("&", StringComparison.Ordinal))
        {
            sb.Append('&');
        }

        var keyParam = target.AbsolutePath.IndexOf("/dl/", StringComparison.OrdinalIgnoreCase) >= 0
            ? JackettApiKeyParam
            : ApiKeyParam;
        sb.Append(keyParam).Append('=').Append(Uri.EscapeDataString(apiKey.Trim()));
        sb.Append(fragment);
        return sb.ToString();
    }

    /// <summary>True when the URL's query already contains an <c>apikey</c> or <c>jackett_apikey</c> parameter.</summary>
    internal static bool HasApiKey(string url)
    {
        if (string.IsNullOrEmpty(url) || !TrySplit(url, out _, out var query, out _))
        {
            return false;
        }

        foreach (var part in query.Split('&'))
        {
            if (IsApiKeyParam(part))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns a copy of <paramref name="url"/> that is safe to log: secret parameter values are masked.</summary>
    internal static string RedactForLog(string url)
    {
        if (string.IsNullOrEmpty(url) || !TrySplit(url, out var prefix, out var query, out var fragment))
        {
            return url;
        }

        var parts = query.Split('&');
        var changed = false;
        for (var i = 0; i < parts.Length; i++)
        {
            foreach (var secret in SecretParams)
            {
                if (IsParam(parts[i], secret))
                {
                    var eq = parts[i].IndexOf('=');
                    parts[i] = (eq >= 0 ? parts[i].Substring(0, eq) : parts[i]) + "=REDACTED";
                    changed = true;
                    break;
                }
            }
        }

        return changed ? prefix + "?" + string.Join("&", parts) + fragment : url;
    }

    private static bool IsApiKeyParam(string part)
    {
        foreach (var name in ApiKeyParams)
        {
            if (IsParam(part, name))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHttp(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

    // Splits "prefix?query#fragment". Returns false when there is no query component.
    private static bool TrySplit(string url, out string prefix, out string query, out string fragment)
    {
        prefix = url;
        query = string.Empty;
        fragment = string.Empty;

        var q = url.IndexOf('?');
        if (q < 0)
        {
            return false;
        }

        var h = url.IndexOf('#');
        if (h >= 0 && h < q)
        {
            // '?' is inside the fragment, so there is no query.
            return false;
        }

        prefix = url.Substring(0, q);
        if (h >= 0)
        {
            query = url.Substring(q + 1, h - q - 1);
            fragment = url.Substring(h);
        }
        else
        {
            query = url.Substring(q + 1);
        }

        return true;
    }

    private static bool IsParam(string part, string name)
    {
        var eq = part.IndexOf('=');
        var rawName = eq >= 0 ? part.Substring(0, eq) : part;
        if (rawName.Length == 0)
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(rawName.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            decoded = rawName;
        }

        return string.Equals(decoded.Trim(), name, StringComparison.OrdinalIgnoreCase);
    }
}
