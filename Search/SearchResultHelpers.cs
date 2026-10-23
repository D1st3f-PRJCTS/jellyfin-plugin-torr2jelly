using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Jellyfin.Plugin.Torr2Jelly.Engine;

namespace Jellyfin.Plugin.Torr2Jelly.Search;

/// <summary>How much of the download a release counts for. Higher is better; providers take the best tier seen.</summary>
internal enum DiscountTier
{
    None = 0,
    Bronze = 1,
    Halfleech = 2,
    Freeleech = 3
}

/// <summary>
/// The closed vocabulary of tags the plugin understands. Anything an indexer reports that is not in
/// <see cref="Aliases"/> (e.g. Prowlarr's <c>scene</c>, <c>nuked</c>, <c>approved</c> flags) is not a tag
/// and is dropped, so it can no longer make an ordinary release look "tagged" or hide it behind a rule.
/// </summary>
internal static class TorrentTags
{
    public const string Freeleech = "freeleech";
    public const string Halfleech = "halfleech";
    public const string Bronze = "bronze";
    public const string Internal = "internal";
    public const string Exclusive = "exclusive";
    public const string DoubleUpload = "doubleupload";
    public const string Regular = "regular";

    // Keys are normalised (see NormalizeKey). Values are the canonical tags the UI and tracker rules use.
    //
    // Discount mapping (documented choice):
    //   freeleech            -> freeleech   (100 % off, "gold")
    //   freeleech75 / 50     -> halfleech   (75 % / 50 % off, "silver"; there is no tier between gold and silver,
    //                                        so a 75 % discount is shown as the nearest lower tier instead of as free)
    //   freeleech25          -> bronze      (25 % off)
    //   gold / silver        -> aliases the UI already treats as freeleech / halfleech
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["freeleech"] = Freeleech,
        ["gold"] = Freeleech,
        ["halfleech"] = Halfleech,
        ["silver"] = Halfleech,
        ["freeleech50"] = Halfleech,
        ["freeleech75"] = Halfleech,
        ["bronze"] = Bronze,
        ["freeleech25"] = Bronze,
        ["internal"] = Internal,
        ["exclusive"] = Exclusive,
        ["doubleupload"] = DoubleUpload,
        ["2xupload"] = DoubleUpload,
        ["regular"] = Regular,
        ["untagged"] = Regular
    };

    /// <summary>Lowercase and remove '_', '-' and whitespace.</summary>
    public static string NormalizeKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (ch == '_' || ch == '-' || char.IsWhiteSpace(ch))
            {
                continue;
            }

            sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.ToString();
    }

    /// <summary>Maps a raw tag/flag to its canonical form. Returns false for unknown tags.</summary>
    public static bool TryCanonicalize(string? raw, out string canonical)
    {
        canonical = string.Empty;
        var key = NormalizeKey(raw);
        if (key.Length == 0 || !Aliases.TryGetValue(key, out var value))
        {
            return false;
        }

        canonical = value;
        return true;
    }
}

/// <summary>
/// Accumulates tags and discount information from every source (flags, tags, volume factors, title)
/// and only then resolves them, so the outcome does not depend on the order the sources arrive in.
/// </summary>
internal sealed class TagCollector
{
    private DiscountTier _tier = DiscountTier.None;
    private bool _doubleUpload;
    private bool _internal;
    private bool _exclusive;

    private bool _exactFreeleechFlag;
    private double? _downloadFactor;

    public bool IsFreeleech => _tier == DiscountTier.Freeleech;

    /// <summary>
    /// Strict freeleech: a download volume factor of exactly 0, or an exact "freeleech" flag. Unlike
    /// <see cref="IsFreeleech"/> (the "gold" tier, which also accepts the "gold" alias) it never reflects a
    /// partial discount.
    /// </summary>
    public bool IsStrictFreeleech => _exactFreeleechFlag || (_downloadFactor.HasValue && _downloadFactor.Value < 1e-9);

    /// <summary>The smallest valid download volume factor seen, or null when the indexer reported none.</summary>
    public double? DownloadVolumeFactor => _downloadFactor;

    /// <summary>Adds a raw indexer tag or flag. Unknown values are ignored.</summary>
    public void AddRaw(string? raw)
    {
        if (string.Equals(TorrentTags.NormalizeKey(raw), TorrentTags.Freeleech, StringComparison.Ordinal))
        {
            _exactFreeleechFlag = true;
        }

        if (!TorrentTags.TryCanonicalize(raw, out var tag))
        {
            return;
        }

        switch (tag)
        {
            case TorrentTags.Freeleech:
                Raise(DiscountTier.Freeleech);
                break;
            case TorrentTags.Halfleech:
                Raise(DiscountTier.Halfleech);
                break;
            case TorrentTags.Bronze:
                Raise(DiscountTier.Bronze);
                break;
            case TorrentTags.Internal:
                _internal = true;
                break;
            case TorrentTags.Exclusive:
                _exclusive = true;
                break;
            case TorrentTags.DoubleUpload:
                _doubleUpload = true;
                break;
            // "regular" is the absence of tags, never a tag itself.
        }
    }

    /// <summary>
    /// Interprets a download volume factor (fraction of the download that counts):
    /// 0 is free, up to 0.5 (this includes 0.25 = "freeleech75") is half-price, below 1 is bronze.
    /// </summary>
    public void AddDownloadFactor(double? factor)
    {
        if (!factor.HasValue || double.IsNaN(factor.Value) || double.IsInfinity(factor.Value) || factor.Value < 0)
        {
            return;
        }

        var f = factor.Value;
        _downloadFactor = _downloadFactor.HasValue ? Math.Min(_downloadFactor.Value, f) : f;
        if (f < 1e-9)
        {
            Raise(DiscountTier.Freeleech);
        }
        else if (f <= 0.5 + 1e-9)
        {
            Raise(DiscountTier.Halfleech);
        }
        else if (f < 1 - 1e-9)
        {
            Raise(DiscountTier.Bronze);
        }
    }

    public void AddUploadFactor(double? factor)
    {
        if (factor.HasValue && !double.IsNaN(factor.Value) && factor.Value > 1)
        {
            _doubleUpload = true;
        }
    }

    /// <summary>Heuristics for trackers that only mark these in the release title.</summary>
    public void AddTitle(string? title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return;
        }

        if (title.Contains("[Авторськ", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Авторський", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Власна роздача", StringComparison.OrdinalIgnoreCase))
        {
            _internal = true;
        }

        if (title.Contains("[Exclusive]", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Ексклюзив", StringComparison.OrdinalIgnoreCase))
        {
            _exclusive = true;
        }
    }

    /// <summary>Canonical tag list: the single best discount tag first, then the special tags.</summary>
    public List<string> ToList()
    {
        var tags = new List<string>(4);
        switch (_tier)
        {
            case DiscountTier.Freeleech:
                tags.Add(TorrentTags.Freeleech);
                break;
            case DiscountTier.Halfleech:
                tags.Add(TorrentTags.Halfleech);
                break;
            case DiscountTier.Bronze:
                tags.Add(TorrentTags.Bronze);
                break;
        }

        if (_internal)
        {
            tags.Add(TorrentTags.Internal);
        }

        if (_exclusive)
        {
            tags.Add(TorrentTags.Exclusive);
        }

        if (_doubleUpload)
        {
            tags.Add(TorrentTags.DoubleUpload);
        }

        return tags;
    }

    private void Raise(DiscountTier tier)
    {
        if (tier > _tier)
        {
            _tier = tier;
        }
    }
}

/// <summary>Culture-independent number parsing for indexer responses (a server running under uk-UA must still read "0.5").</summary>
internal static class SearchParse
{
    public static bool TryDouble(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim();
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            !(s.IndexOf(',') >= 0 && s.IndexOf('.') < 0 &&
              double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value)))
        {
            value = 0;
            return false;
        }

        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            value = 0;
            return false;
        }

        return true;
    }

    public static bool TryLong(string? text, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim();
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        // Some indexers report sizes like "1.5E9" or "1234.0".
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) &&
            !double.IsNaN(d) && !double.IsInfinity(d) && d >= 0 && d < long.MaxValue)
        {
            value = (long)d;
            return true;
        }

        value = 0;
        return false;
    }

    public static bool TryInt(string? text, out int value)
    {
        value = 0;
        if (!TryLong(text, out var l) || l < int.MinValue || l > int.MaxValue)
        {
            return false;
        }

        value = (int)l;
        return true;
    }
}

/// <summary>Info-hash and magnet helpers shared by the providers.</summary>
internal static class InfoHashHelper
{
    private const string BtihPrefix = "urn:btih:";

    /// <summary>
    /// Returns the lowercase hex info-hash, or empty when <paramref name="raw"/> is not a valid hash.
    /// Accepts 40/64 hex digits and the 32-character base32 form some magnets use.
    /// </summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var s = raw.Trim();
        if (s.StartsWith(BtihPrefix, StringComparison.OrdinalIgnoreCase))
        {
            s = s.Substring(BtihPrefix.Length);
        }

        if (TorrentHash.IsValid(s))
        {
            return TorrentHash.Normalize(s);
        }

        if (s.Length == 32 && TryDecodeBase32(s, out var hex))
        {
            return hex;
        }

        return string.Empty;
    }

    /// <summary>Extracts the <c>btih</c> info-hash from a magnet link, or returns empty.</summary>
    public static string FromMagnet(string? magnet)
    {
        if (string.IsNullOrWhiteSpace(magnet) || !magnet.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var q = magnet.IndexOf('?');
        if (q < 0)
        {
            return string.Empty;
        }

        foreach (var part in magnet.Substring(q + 1).Split('&'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var name = part.Substring(0, eq);
            if (!name.Equals("xt", StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith("xt.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string value;
            try
            {
                value = Uri.UnescapeDataString(part.Substring(eq + 1));
            }
            catch (UriFormatException)
            {
                continue;
            }

            if (value.StartsWith(BtihPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var hash = Normalize(value);
                if (hash.Length > 0)
                {
                    return hash;
                }
            }
        }

        return string.Empty;
    }

    /// <summary><c>magnet:?xt=urn:btih:{hash}&amp;dn={urlencoded title}</c></summary>
    public static string BuildMagnet(string hash, string? title)
    {
        var magnet = "magnet:?xt=urn:btih:" + hash;
        if (!string.IsNullOrWhiteSpace(title))
        {
            magnet += "&dn=" + Uri.EscapeDataString(title.Trim());
        }

        return magnet;
    }

    private static bool TryDecodeBase32(string s, out string hex)
    {
        hex = string.Empty;
        var bytes = new byte[20];
        var buffer = 0;
        var bits = 0;
        var idx = 0;
        foreach (var ch in s)
        {
            int v;
            if (ch >= 'A' && ch <= 'Z')
            {
                v = ch - 'A';
            }
            else if (ch >= 'a' && ch <= 'z')
            {
                v = ch - 'a';
            }
            else if (ch >= '2' && ch <= '7')
            {
                v = ch - '2' + 26;
            }
            else
            {
                return false;
            }

            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                if (idx >= bytes.Length)
                {
                    return false;
                }

                bytes[idx++] = (byte)((buffer >> bits) & 0xFF);
                buffer &= (1 << bits) - 1;
            }
        }

        if (idx != bytes.Length)
        {
            return false;
        }

        hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return true;
    }
}
