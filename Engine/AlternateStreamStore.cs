using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torr2Jelly.Engine;

/// <summary>
/// One alternate version of an existing item: the torrent lives in <see cref="TargetDir"/> next to the
/// item's primary stream instead of in a standalone folder of its own.
/// </summary>
public sealed class AlternateStreamEntry
{
    [JsonPropertyName("targetDir")]
    public string TargetDir { get; init; } = string.Empty;

    [JsonPropertyName("itemId")]
    public Guid? ItemId { get; init; }

    [JsonPropertyName("strmPath")]
    public string StrmPath { get; init; } = string.Empty;
}

/// <summary>
/// Persists <c>hash -> alternate stream</c> state so that sync runs never turn an alternate version into a
/// standalone item (and never delete it from the folder of the item it belongs to).
/// The file is <c>{DataPath}/torr2jelly/alternate-streams.json</c>. A missing or corrupt file is treated as empty.
/// </summary>
public sealed class AlternateStreamStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly ILogger _logger;
    private Dictionary<string, AlternateStreamEntry>? _entries;

    public AlternateStreamStore(string dataPath, ILogger logger)
    {
        _filePath = Path.Combine(dataPath, "torr2jelly", "alternate-streams.json");
        _logger = logger;
    }

    public bool Contains(string hash)
    {
        lock (_gate)
        {
            return Load().ContainsKey(TorrentHash.Normalize(hash));
        }
    }

    public bool TryGet(string hash, out AlternateStreamEntry entry)
    {
        lock (_gate)
        {
            if (Load().TryGetValue(TorrentHash.Normalize(hash), out var found))
            {
                entry = found;
                return true;
            }
        }

        entry = new AlternateStreamEntry();
        return false;
    }

    /// <summary>Adds or replaces the entry for <paramref name="hash"/>. Invalid hashes are ignored.</summary>
    public void Register(string hash, string targetDir, Guid? itemId, string strmPath)
    {
        var key = TorrentHash.Normalize(hash);
        if (!TorrentHash.IsValid(key) || string.IsNullOrWhiteSpace(targetDir))
        {
            return;
        }

        lock (_gate)
        {
            Load()[key] = new AlternateStreamEntry { TargetDir = targetDir, ItemId = itemId, StrmPath = strmPath };
            Save();
        }
    }

    /// <summary>Adds the entry only if the hash is not registered yet. Returns true when an entry was added.</summary>
    public bool RegisterIfMissing(string hash, string targetDir, Guid? itemId, string strmPath)
    {
        var key = TorrentHash.Normalize(hash);
        if (!TorrentHash.IsValid(key) || string.IsNullOrWhiteSpace(targetDir))
        {
            return false;
        }

        lock (_gate)
        {
            var entries = Load();
            if (entries.ContainsKey(key))
            {
                return false;
            }

            entries[key] = new AlternateStreamEntry { TargetDir = targetDir, ItemId = itemId, StrmPath = strmPath };
            Save();
            return true;
        }
    }

    public bool Unregister(string hash)
    {
        lock (_gate)
        {
            if (!Load().Remove(TorrentHash.Normalize(hash)))
            {
                return false;
            }

            Save();
            return true;
        }
    }

    /// <summary>Removes every entry whose hash matches <paramref name="predicate"/>. Returns the number removed.</summary>
    public int RemoveWhere(Func<string, bool> predicate)
    {
        lock (_gate)
        {
            var entries = Load();
            var doomed = entries.Keys.Where(predicate).ToList();
            if (doomed.Count == 0)
            {
                return 0;
            }

            foreach (var key in doomed)
            {
                entries.Remove(key);
            }

            Save();
            return doomed.Count;
        }
    }

    // Must be called with _gate held.
    private Dictionary<string, AlternateStreamEntry> Load()
    {
        if (_entries != null)
        {
            return _entries;
        }

        var entries = new Dictionary<string, AlternateStreamEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath, Encoding.UTF8);
                var parsed = JsonSerializer.Deserialize<Dictionary<string, AlternateStreamEntry>>(json, JsonOptions);
                if (parsed != null)
                {
                    foreach (var (hash, entry) in parsed)
                    {
                        var key = TorrentHash.Normalize(hash ?? string.Empty);
                        if (entry != null && TorrentHash.IsValid(key) && !string.IsNullOrWhiteSpace(entry.TargetDir))
                        {
                            entries[key] = entry;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Corrupt or unreadable: start empty. The legacy-alternate migration in FullSyncAsync rebuilds most of it.
            _logger.LogWarning(ex, "Could not read alternate stream state from {File}; starting with an empty state", _filePath);
            TryPreserveCorruptFile();
        }

        _entries = entries;
        return entries;
    }

    // Must be called with _gate held.
    private void Save()
    {
        if (_entries == null)
        {
            return;
        }

        string? tmp = null;
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(_entries, JsonOptions);
            tmp = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, _filePath, overwrite: true);
            tmp = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save alternate stream state to {File}", _filePath);
        }
        finally
        {
            if (tmp != null)
            {
                try
                {
                    File.Delete(tmp);
                }
                catch
                {
                    // best effort
                }
            }
        }
    }

    private void TryPreserveCorruptFile()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                File.Move(_filePath, _filePath + ".corrupt", overwrite: true);
            }
        }
        catch
        {
            // best effort; the next Save() overwrites the file anyway
        }
    }
}
