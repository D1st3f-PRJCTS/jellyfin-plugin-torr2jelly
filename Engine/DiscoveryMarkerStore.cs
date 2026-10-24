using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Torr2Jelly.Engine;

internal sealed record DiscoveryMarker(string State, Guid Nonce, DateTime AtUtc)
{
    [JsonIgnore] public string OwnerCategory => "t2j-pending-" + Nonce.ToString("N");
    public bool Owns(TorrTorrent torrent) => State == "pending" && Nonce != Guid.Empty &&
        string.Equals(torrent.Category, OwnerCategory, StringComparison.Ordinal);
}

/// <summary>Suppression is separate from ownership. Rejected hashes never grant permission to remove a torrent.</summary>
internal sealed class DiscoveryMarkerStore(string dataPath)
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = Gates.GetOrAdd(Path.GetFullPath(dataPath), _ => new object());
    private static readonly TimeSpan RejectionTtl = TimeSpan.FromMinutes(15);
    internal string DirectoryPath => Path.Combine(dataPath, "torr2jelly-discovery-pending");

    internal string PathFor(string hash)
    {
        if (!TorrentHash.IsValid(hash)) throw new ArgumentException("Invalid torrent hash.", nameof(hash));
        return Path.Combine(DirectoryPath, TorrentHash.Normalize(hash));
    }

    internal DiscoveryMarker? Read(string hash)
    {
        lock (_gate)
        {
            var path = PathFor(hash);
            if (!File.Exists(path)) return null;
            DiscoveryMarker? marker = null;
            try { if (new FileInfo(path).Length <= 2048) marker = JsonSerializer.Deserialize<DiscoveryMarker>(File.ReadAllText(path)); }
            catch (JsonException) { }
            // Legacy/invalid markers cannot prove a completed rollback. Suppress until
            // absence is confirmed or manual intake adopts the hash; never grant ownership.
            if (marker?.State is not ("pending" or "rejected")) marker = new("pending", Guid.Empty, File.GetLastWriteTimeUtc(path));
            if (marker.State == "rejected" && DateTime.UtcNow - marker.AtUtc >= RejectionTtl && !File.Exists(path + ".rollback.json"))
            { File.Delete(path); return null; }
            return marker;
        }
    }

    internal DiscoveryMarker Begin(string hash)
    {
        lock (_gate)
        {
            var marker = new DiscoveryMarker("pending", Guid.NewGuid(), DateTime.UtcNow);
            Write(hash, marker);
            return marker;
        }
    }

    internal bool IsCurrent(string hash, Guid nonce) { lock (_gate) return Read(hash) is { State: "pending" } marker && marker.Nonce == nonce; }

    internal void Reject(string hash, Guid nonce)
    {
        lock (_gate)
            if (IsCurrent(hash, nonce)) Write(hash, new("rejected", nonce, DateTime.UtcNow));
    }

    internal void Complete(string hash, Guid nonce)
    {
        lock (_gate)
            if (IsCurrent(hash, nonce)) File.Delete(PathFor(hash));
    }

    internal void ClearForManual(string hash)
    {
        lock (_gate)
        {
            var path = PathFor(hash);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private void Write(string hash, DiscoveryMarker marker)
    {
        Directory.CreateDirectory(DirectoryPath);
        var path = PathFor(hash);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(marker)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
