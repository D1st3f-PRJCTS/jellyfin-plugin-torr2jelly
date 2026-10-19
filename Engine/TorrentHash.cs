using System;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Torr2Jelly.Engine;

/// <summary>
/// Strict helpers for BitTorrent info-hashes. Every place that matches a .strm file to a torrent
/// must go through <see cref="TryExtractFromStrm"/> and compare with <see cref="Equal"/> —
/// never substring-match raw file content against a user-supplied hash.
/// </summary>
public static class TorrentHash
{
    // v1 info-hash = 40 hex chars (SHA-1), v2 = 64 hex chars (SHA-256).
    private static readonly Regex HashRegex = new(@"^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$", RegexOptions.Compiled);

    // Matches the link= query parameter of a TorrServer stream URL as written by StrmEngine:
    // {base}/stream?link={hash}&index={id}&play
    private static readonly Regex StrmLinkRegex = new(@"[?&]link=([0-9a-fA-F]{64}|[0-9a-fA-F]{40})(?=[&#\s]|$)", RegexOptions.Compiled);

    public static bool IsValid(string? hash) => !string.IsNullOrEmpty(hash) && HashRegex.IsMatch(hash);

    public static string Normalize(string hash) => hash.Trim().ToLowerInvariant();

    public static bool Equal(string? a, string? b) =>
        a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Extracts the torrent hash from the content of a .strm file written by this plugin.</summary>
    public static bool TryExtractFromStrm(string? strmContent, out string hash)
    {
        hash = string.Empty;
        if (string.IsNullOrWhiteSpace(strmContent))
        {
            return false;
        }

        var match = StrmLinkRegex.Match(strmContent);
        if (!match.Success)
        {
            return false;
        }

        hash = Normalize(match.Groups[1].Value);
        return true;
    }
}
