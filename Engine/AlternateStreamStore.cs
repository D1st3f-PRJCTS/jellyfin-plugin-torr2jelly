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
/// A torrent that lives inside the folder of an existing library item instead of in a standalone folder of its own.
/// <see cref="Kind"/> says how: an <see cref="AlternateStreamEntry.KindAlternate"/> version sits next to the item's
/// primary stream (movies); a <see cref="AlternateStreamEntry.KindSeries"/> release has its episodes written into
/// the season folders of an existing series, which <see cref="TargetDir"/> is.
/// </summary>
public sealed class AlternateStreamEntry
{
    /// <summary>An alternate version of a movie (the default; also what entries written by older versions are).</summary>
    public const string KindAlternate = "alternate";

    /// <summary>A release whose episodes are merged into an existing series folder.</summary>
    public const string KindSeries = "series";

    [JsonPropertyName("targetDir")]
    public string TargetDir { get; init; } = string.Empty;

    [JsonPropertyName("itemId")]
    public Guid? ItemId { get; init; }

    [JsonPropertyName("strmPath")]
    public string StrmPath { get; init; } = string.Empty;

    /// <summary><see cref="KindAlternate"/> or <see cref="KindSeries"/>; a missing or unknown value reads as alternate.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = KindAlternate;

    [JsonIgnore]
    public bool IsSeries => string.Equals(Kind, KindSeries, StringComparison.OrdinalIgnoreCase);

    internal static string NormalizeKind(string? kind) =>
        string.Equals(kind?.Trim(), KindSeries, StringComparison.OrdinalIgnoreCase) ? KindSeries : KindAlternate;
}

/// <summary>
/// Persists <c>hash -> alternate stream / series torrent</c> state so that sync runs never turn an alternate version
/// into a standalone item (and never delete it from the folder of the item it belongs to), and keep re-materialising
/// a series torrent inside its series folder.
/// The file is <c>{DataPath}/torr2jelly/alternate-streams.json</c>. A missing or corrupt file is treated as empty;
/// entries without a <c>kind</c> (written by older versions) are alternates.
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

    internal bool IsPersisted(string hash)
    {
        lock (_gate)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_filePath));
                return doc.RootElement.TryGetProperty(TorrentHash.Normalize(hash), out var entry) &&
                    entry.TryGetProperty("strmPath", out var path) && !string.IsNullOrWhiteSpace(path.GetString());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
        }
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

    /// <summary>
    /// Adds or replaces the entry for <paramref name="hash"/>. Invalid hashes are ignored.
    /// <paramref name="kind"/> is <see cref="AlternateStreamEntry.KindAlternate"/> unless stated otherwise.
    /// </summary>
    public void Register(string hash, string targetDir, Guid? itemId, string strmPath, string kind = AlternateStreamEntry.KindAlternate)
    {
        var key = TorrentHash.Normalize(hash);
        if (!TorrentHash.IsValid(key) || string.IsNullOrWhiteSpace(targetDir))
        {
            return;
        }

        lock (_gate)
        {
            Load()[key] = new AlternateStreamEntry
            {
                TargetDir = targetDir,
                ItemId = itemId,
                StrmPath = strmPath ?? string.Empty,
                Kind = AlternateStreamEntry.NormalizeKind(kind)
            };
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
                            // A missing, null or unknown kind is an alternate: the safe reading (such an entry is
                            // only ever left alone).
                            entries[key] = string.Equals(entry.Kind, AlternateStreamEntry.NormalizeKind(entry.Kind), StringComparison.Ordinal)
                                ? entry
                                : new AlternateStreamEntry
                                {
                                    TargetDir = entry.TargetDir,
                                    ItemId = entry.ItemId,
                                    StrmPath = entry.StrmPath ?? string.Empty,
                                    Kind = AlternateStreamEntry.NormalizeKind(entry.Kind)
                                };
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
