using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.Torr2Jelly.Engine;

/// <summary>Reads names and lengths without starting a torrent. Unsupported/ambiguous metadata fails closed.</summary>
public sealed record TorrentMetadata(string Hash, string Name, List<TorrTorrentFile> Files)
{
    public static TorrentMetadata? Read(byte[] bytes)
    {
        if (TorrentFile.Validate("release.torrent", bytes) != null || !TorrentFile.TryGetInfoHash(bytes, out var hash)) return null;
        try
        {
            var reader = new Reader(bytes);
            var root = reader.Value() as Dictionary<string, object>;
            if (root == null || !root.TryGetValue("info", out var infoObject) || infoObject is not Dictionary<string, object> info) return null;
            // v2 file trees and hybrid padding need separate indexing rules; never guess their stream index.
            if (info.ContainsKey("meta version")) return null;
            var name = Text(info, "name.utf-8") ?? Text(info, "name");
            if (!SafeSegment(name)) return null;
            var files = new List<TorrTorrentFile>();
            if (info.TryGetValue("files", out var entries))
            {
                if (info.ContainsKey("length") || entries is not List<object> list || list.Count is 0 or > 10000) return null;
                foreach (var entry in list)
                {
                    if (entry is not Dictionary<string, object> f || !f.TryGetValue("length", out var len) || len is not long length || length < 0) return null;
                    var path = f.GetValueOrDefault("path.utf-8") ?? f.GetValueOrDefault("path");
                    if (path is not List<object> parts || parts.Count is 0 or > 32) return null;
                    var names = parts.Select(p => p as string).ToList();
                    if (names.Any(p => !SafeSegment(p))) return null;
                    files.Add(new TorrTorrentFile { Id = files.Count + 1, Path = name + "/" + string.Join('/', names), Length = length });
                }
            }
            else
            {
                if (!info.TryGetValue("length", out var len) || len is not long length || length <= 0) return null;
                files.Add(new TorrTorrentFile { Id = 1, Path = name!, Length = length });
            }
            if (files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count) return null;
            return new TorrentMetadata(hash, name!, files);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or DecoderFallbackException) { return null; }
    }

    private static string? Text(Dictionary<string, object> d, string k) => d.GetValueOrDefault(k) as string;
    private static bool SafeSegment(string? text) => !string.IsNullOrEmpty(text) && text.Length <= 1024 && text is not ("." or "..") && !text.Any(c => char.IsControl(c) || c is '/' or '\\' or ':');

    private sealed class Reader(byte[] data)
    {
        private int _at;
        private int _nodes;
        private static readonly UTF8Encoding Utf8 = new(false, true);

        public object Value(int depth = 0)
        {
            if (depth > 32 || ++_nodes > 100000 || _at >= data.Length) throw new FormatException();
            var token = data[_at++];
            if (token == 'd')
            {
                var dict = new Dictionary<string, object>(StringComparer.Ordinal);
                while (data[_at] != 'e')
                {
                    var key = Value(depth + 1) as string ?? throw new FormatException();
                    // pieces are binary; no reason to decode/store them as text.
                    if (key is "pieces" or "piece layers") { Skip(depth + 1); continue; }
                    if (!dict.TryAdd(key, Value(depth + 1))) throw new FormatException();
                }
                _at++;
                return dict;
            }
            if (token == 'l')
            {
                var list = new List<object>();
                while (data[_at] != 'e') list.Add(Value(depth + 1));
                _at++;
                return list;
            }
            if (token == 'i')
            {
                var end = System.Array.IndexOf(data, (byte)'e', _at);
                var number = long.Parse(Encoding.ASCII.GetString(data, _at, end - _at), CultureInfo.InvariantCulture);
                _at = end + 1;
                return number;
            }
            _at--;
            var (start, length) = StringSpan();
            return Utf8.GetString(data, start, length);
        }

        private (int, int) StringSpan()
        {
            var colon = System.Array.IndexOf(data, (byte)':', _at);
            var length = int.Parse(Encoding.ASCII.GetString(data, _at, colon - _at), CultureInfo.InvariantCulture);
            var start = colon + 1;
            _at = checked(start + length);
            return (start, length);
        }

        private void Skip(int depth)
        {
            if (depth > 32 || ++_nodes > 100000) throw new FormatException();
            var token = data[_at];
            if (token is (byte)'d' or (byte)'l')
            {
                _at++;
                while (data[_at] != 'e') Skip(depth + 1);
                _at++;
            }
            else if (token == 'i') _at = System.Array.IndexOf(data, (byte)'e', _at) + 1;
            else StringSpan();
        }
    }
}
