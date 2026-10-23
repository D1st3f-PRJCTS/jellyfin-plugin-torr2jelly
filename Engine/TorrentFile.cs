using System;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Torr2Jelly.Engine;

/// <summary>
/// Checks for an uploaded .torrent file and a small bencode reader that finds the <c>info</c> dictionary, so the v1
/// info-hash (SHA-1 of the raw bencoded <c>info</c> value) can be computed without any extra package.
/// </summary>
public static class TorrentFile
{
    /// <summary>Largest .torrent file accepted (a torrent with tens of thousands of files is well below this).</summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    public const string CodeInvalid = "invalid_torrent_file";
    public const string CodeTooLarge = "file_too_large";
    public const string CodeRequired = "file_required";

    private const int MaxDepth = 32;

    /// <summary>
    /// Returns null when <paramref name="data"/> is an acceptable .torrent file, otherwise a stable error code. The
    /// client's file name is only checked for the <c>.torrent</c> extension; it is never used for anything else.
    /// </summary>
    public static string? Validate(string? fileName, byte[]? data)
    {
        if (data == null)
        {
            return CodeRequired;
        }

        if (data.Length > MaxBytes)
        {
            return CodeTooLarge;
        }

        if (data.Length == 0 || string.IsNullOrWhiteSpace(fileName) ||
            !fileName.Trim().EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
        {
            return CodeInvalid;
        }

        return TryFindInfo(data, out _, out _) ? null : CodeInvalid;
    }

    /// <summary>The lowercase hex v1 info-hash (SHA-1 of the bencoded <c>info</c> value), or false for a malformed file.</summary>
    public static bool TryGetInfoHash(byte[] data, out string hash)
    {
        hash = string.Empty;
        if (!TryFindInfo(data, out var start, out var end))
        {
            return false;
        }

        hash = Convert.ToHexString(SHA1.HashData(data.AsSpan(start, end - start))).ToLowerInvariant();
        return true;
    }

    /// <summary>
    /// True when <paramref name="data"/> is one well-formed bencoded dictionary (nothing before or after it) whose
    /// top-level <c>info</c> value is a dictionary; <paramref name="start"/> / <paramref name="end"/> delimit that value.
    /// </summary>
    internal static bool TryFindInfo(byte[] data, out int start, out int end)
    {
        start = 0;
        end = 0;
        if (data.Length < 2 || data[0] != (byte)'d')
        {
            return false;
        }

        var pos = 1;
        var infoStart = -1;
        var infoEnd = -1;
        while (true)
        {
            if (pos >= data.Length)
            {
                return false;
            }

            if (data[pos] == (byte)'e')
            {
                pos++;
                break;
            }

            if (!TryReadString(data, ref pos, out var keyStart, out var keyLength))
            {
                return false;
            }

            var valueStart = pos;
            if (!TrySkip(data, ref pos, 1))
            {
                return false;
            }

            if (keyLength == 4 && Encoding.ASCII.GetString(data, keyStart, 4) == "info")
            {
                if (data[valueStart] != (byte)'d')
                {
                    return false;
                }

                infoStart = valueStart;
                infoEnd = pos;
            }
        }

        if (pos != data.Length || infoStart < 0)
        {
            return false;
        }

        start = infoStart;
        end = infoEnd;
        return true;
    }

    private static bool TryReadString(byte[] d, ref int pos, out int start, out int length)
    {
        start = 0;
        length = 0;
        var p = pos;
        long len = 0;
        var digits = 0;
        while (p < d.Length && d[p] >= (byte)'0' && d[p] <= (byte)'9')
        {
            len = (len * 10) + (d[p] - (byte)'0');
            if (++digits > 10)
            {
                return false;
            }

            p++;
        }

        if (digits == 0 || p >= d.Length || d[p] != (byte)':')
        {
            return false;
        }

        p++;
        if (len > d.Length - p)
        {
            return false;
        }

        start = p;
        length = (int)len;
        pos = p + length;
        return true;
    }

    // Moves pos past one bencoded value. Depth-limited, so a hostile file cannot overflow the stack.
    private static bool TrySkip(byte[] d, ref int pos, int depth)
    {
        if (depth > MaxDepth || pos >= d.Length)
        {
            return false;
        }

        var c = d[pos];
        if (c == (byte)'i')
        {
            var p = pos + 1;
            if (p < d.Length && d[p] == (byte)'-')
            {
                p++;
            }

            var digitsStart = p;
            while (p < d.Length && d[p] >= (byte)'0' && d[p] <= (byte)'9')
            {
                p++;
            }

            if (p == digitsStart || p - digitsStart > 18 || p >= d.Length || d[p] != (byte)'e')
            {
                return false;
            }

            pos = p + 1;
            return true;
        }

        if (c >= (byte)'0' && c <= (byte)'9')
        {
            return TryReadString(d, ref pos, out _, out _);
        }

        if (c == (byte)'l')
        {
            pos++;
            while (true)
            {
                if (pos >= d.Length)
                {
                    return false;
                }

                if (d[pos] == (byte)'e')
                {
                    pos++;
                    return true;
                }

                if (!TrySkip(d, ref pos, depth + 1))
                {
                    return false;
                }
            }
        }

        if (c == (byte)'d')
        {
            pos++;
            while (true)
            {
                if (pos >= d.Length)
                {
                    return false;
                }

                if (d[pos] == (byte)'e')
                {
                    pos++;
                    return true;
                }

                if (!TryReadString(d, ref pos, out _, out _) || !TrySkip(d, ref pos, depth + 1))
                {
                    return false;
                }
            }
        }

        return false;
    }
}
