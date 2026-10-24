using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Torr2Jelly.Configuration;
using Jellyfin.Plugin.Torr2Jelly.Daemon;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torr2Jelly.Engine;

public class TorrTorrentFile
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("length")]
    public long Length { get; set; }
}

public class TorrTorrent
{
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("poster")]
    public string? Poster { get; set; }

    [JsonPropertyName("data")]
    public string? Data { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("stat")]
    public int Stat { get; set; }

    [JsonPropertyName("stat_string")]
    public string StatString { get; set; } = string.Empty;

    [JsonPropertyName("torrent_size")]
    public long TorrentSize { get; set; }

    [JsonPropertyName("loaded_size")]
    public long LoadedSize { get; set; }

    // Live swarm numbers; TorrServer reports them only while the torrent is active (absent or 0 otherwise).
    [JsonPropertyName("connected_seeders")]
    public int? ConnectedSeeders { get; set; }

    [JsonPropertyName("active_peers")]
    public int? ActivePeers { get; set; }

    [JsonPropertyName("total_peers")]
    public int? TotalPeers { get; set; }

    [JsonPropertyName("file_stats")]
    public List<TorrTorrentFile>? FileStats { get; set; }

    public List<TorrTorrentFile> GetEffectiveFiles()
    {
        if (FileStats != null && FileStats.Count > 0)
        {
            return FileStats;
        }

        if (!string.IsNullOrWhiteSpace(Data))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(Data);
                if (doc.RootElement.TryGetProperty("TorrServer", out var tsProp) &&
                    tsProp.TryGetProperty("Files", out var filesProp) &&
                    filesProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var files = new List<TorrTorrentFile>();
                    foreach (var f in filesProp.EnumerateArray())
                    {
                        var id = f.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0;
                        var path = f.TryGetProperty("path", out var pathProp) ? pathProp.GetString() ?? string.Empty : string.Empty;
                        var length = f.TryGetProperty("length", out var lenProp) ? lenProp.GetInt64() : 0;
                        files.Add(new TorrTorrentFile { Id = id, Path = path, Length = length });
                    }
                    if (files.Count > 0)
                    {
                        return files;
                    }
                }
            }
            catch { }
        }

        return FileStats ?? new List<TorrTorrentFile>();
    }
}

public class TorrClientService
{
    // "add" may have to fetch a .torrent file or resolve a magnet; everything else is a quick local lookup.
    private static readonly TimeSpan AddTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    // Hard ceiling of the underlying client. The per-call timeouts above are enforced with a linked
    // CancellationTokenSource, so this only has to be larger than the longest of them.
    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(75);

    private static readonly JsonSerializerOptions ListJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger<TorrClientService> _logger;
    private readonly HttpClient _httpClient;
    private readonly TorrServerDaemonManager _daemonManager;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly DiscoveryMarkerStore? _discoveryMarkers;

    public TorrClientService(ILogger<TorrClientService> logger, IHttpClientFactory httpClientFactory,
        TorrServerDaemonManager daemonManager, MediaBrowser.Common.Configuration.IApplicationPaths paths)
        : this(logger, httpClientFactory, daemonManager)
    {
        _discoveryMarkers = new DiscoveryMarkerStore(paths.DataPath);
    }

    public TorrClientService(
        ILogger<TorrClientService> logger,
        IHttpClientFactory httpClientFactory,
        TorrServerDaemonManager daemonManager)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("TorrClient");
        _httpClient.Timeout = ClientTimeout;
        _daemonManager = daemonManager;
    }

    private string GetBaseUrl()
    {
        return _daemonManager.GetEffectiveUrl();
    }

    /// <summary>
    /// POSTs a JSON payload to TorrServer. The call is cancelled after <paramref name="timeout"/>, or when
    /// <paramref name="cancellationToken"/> is cancelled. Callers dispose the response.
    /// </summary>
    private async Task<HttpResponseMessage> PostAsync(string path, object payload, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            return await _httpClient.PostAsJsonAsync($"{GetBaseUrl()}{path}", payload, cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Not the caller: the per-call timeout fired.
            throw new TimeoutException($"TorrServer did not answer within {timeout.TotalSeconds:0} s.");
        }
    }

    private async Task<HttpResponseMessage> PostMultipartAsync(string path, HttpContent content, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            return await _httpClient.PostAsync($"{GetBaseUrl()}{path}", content, cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"TorrServer did not answer within {timeout.TotalSeconds:0} s.");
        }
    }

    /// <summary>
    /// Lists the torrents stored in TorrServer. Returns <c>null</c> on any failure (unreachable, timeout,
    /// non-success status, unreadable body) so callers can tell "TorrServer has no torrents" (empty list) from
    /// "TorrServer could not be asked" and never act on an empty list that is really an error.
    /// </summary>
    public async Task<List<TorrTorrent>?> ListTorrentsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await PostAsync("/torrents", new { action = "list" }, DefaultTimeout, cancellationToken);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
            {
                _logger.LogError("TorrServer returned an empty body for the torrent list");
                return null;
            }

            // TorrServer is written in Go and encodes a nil slice as JSON null: that is the answer for "no torrents".
            if (string.Equals(body.Trim(), "null", StringComparison.Ordinal))
            {
                return new List<TorrTorrent>();
            }

            var list = JsonSerializer.Deserialize<List<TorrTorrent>>(body, ListJsonOptions);
            if (list == null)
            {
                _logger.LogError("TorrServer returned an unreadable torrent list");
                return null;
            }

            list.RemoveAll(t => t == null);
            return list;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list torrents from TorrServer at {Url}", GetBaseUrl());
            return null;
        }
    }

    public async Task<TorrTorrent?> GetTorrentAsync(string hash, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await PostAsync("/torrents", new { action = "get", hash }, DefaultTimeout, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<TorrTorrent>(cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get torrent {Hash} from TorrServer", hash);
            return null;
        }
    }

    /// <summary>
    /// Adds a torrent (magnet, http(s) .torrent link or hash) to TorrServer. Returns <c>null</c> when TorrServer
    /// could not be reached or did not accept the torrent; the cause is logged, never returned to the caller.
    /// </summary>
    private async Task<TorrTorrent?> AddTorrentCoreAsync(
        string link,
        string? title = null,
        string? poster = null,
        string? category = null,
        bool saveToDb = true,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            action = "add",
            link,
            title = title ?? string.Empty,
            poster = poster ?? string.Empty,
            category = category ?? string.Empty,
            save_to_db = saveToDb
        };

        try
        {
            using var response = await PostAsync("/torrents", payload, AddTimeout, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("TorrServer rejected the torrent with HTTP {Status}", (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<TorrTorrent>(cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add torrent to TorrServer");
            return null;
        }
    }

    /// <summary>
    /// Adds a torrent from the bytes of a .torrent file (kept as a file, so the trackers and passkeys of a private
    /// torrent survive) through TorrServer's <c>/torrent/upload</c>. The client's file name is never sent. Returns
    /// <c>null</c> when TorrServer could not be reached or did not accept the file. When the answer carries no
    /// info-hash, the v1 hash is computed from the file and the torrent is fetched with <c>get</c>.
    /// </summary>
    private async Task<TorrTorrent?> AddTorrentFileCoreAsync(
        byte[] file,
        string? title = null,
        string? poster = null,
        string? category = null,
        bool saveToDb = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(file);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-bittorrent");
            content.Add(fileContent, "file", "upload.torrent");
            content.Add(new StringContent(saveToDb ? "true" : "false"), "save");
            if (!string.IsNullOrWhiteSpace(title)) content.Add(new StringContent(title), "title");
            if (!string.IsNullOrWhiteSpace(category)) content.Add(new StringContent(category), "category");
            if (!string.IsNullOrWhiteSpace(poster)) content.Add(new StringContent(poster), "poster");

            using var response = await PostMultipartAsync("/torrent/upload", content, AddTimeout, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("TorrServer rejected the uploaded torrent file with HTTP {Status}", (int)response.StatusCode);
                return null;
            }

            var torrent = ParseUploadResponse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (torrent != null && TorrentHash.IsValid(torrent.Hash))
            {
                return torrent;
            }

            // No usable hash in the answer: derive it from the file and ask TorrServer for the torrent.
            if (!TorrentFile.TryGetInfoHash(file, out var hash))
            {
                return null;
            }

            var fetched = await GetTorrentAsync(hash, cancellationToken);
            if (fetched != null && !TorrentHash.IsValid(fetched.Hash))
            {
                fetched.Hash = hash;
            }

            return fetched;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload a torrent file to TorrServer");
            return null;
        }
    }

    /// <summary>Reads TorrServer's answer to an upload: one torrent object, or an array (the first element is taken).</summary>
    internal static TorrTorrent? ParseUploadResponse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var element = doc.RootElement;
            if (element.ValueKind == JsonValueKind.Array)
            {
                if (element.GetArrayLength() == 0)
                {
                    return null;
                }

                element = element[0];
            }

            return element.ValueKind == JsonValueKind.Object ? element.Deserialize<TorrTorrent>(ListJsonOptions) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<bool> SetTorrentCategoryCoreAsync(
        string hash,
        string category,
        string? title = null,
        string? poster = null,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            action = "set",
            hash,
            category = category ?? string.Empty,
            title = title ?? string.Empty,
            poster = poster ?? string.Empty
        };

        try
        {
            using var response = await PostAsync("/torrents", payload, DefaultTimeout, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set category '{Category}' on torrent {Hash}", category, hash);
            return false;
        }
    }

    private async Task<bool> RemoveTorrentCoreAsync(string hash, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await PostAsync("/torrents", new { action = "rem", hash }, DefaultTimeout, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove torrent {Hash} from TorrServer", hash);
            return false;
        }
    }

    // Every plugin mutation shares this gate. Manual intake revokes discovery's lease before
    // releasing it, including adds that intentionally skip library sync or have no category.
    public async Task<TorrTorrent?> AddTorrentAsync(string link, string? title = null, string? poster = null,
        string? category = null, bool saveToDb = true, CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var knownHash = TorrentHash.IsValid(link) ? link : Search.InfoHashHelper.FromMagnet(link);
            if (TorrentHash.IsValid(knownHash)) _discoveryMarkers?.ClearForManual(knownHash);
            var added = await AddTorrentCoreAsync(link, title, poster, category, saveToDb, cancellationToken);
            if (added != null && TorrentHash.IsValid(added.Hash)) _discoveryMarkers?.ClearForManual(added.Hash);
            return added;
        }
        finally { _mutationGate.Release(); }
    }

    public async Task<TorrTorrent?> AddTorrentFileAsync(byte[] file, string? title = null, string? poster = null,
        string? category = null, bool saveToDb = true, CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            if (TorrentFile.TryGetInfoHash(file, out var hash)) _discoveryMarkers?.ClearForManual(hash);
            var added = await AddTorrentFileCoreAsync(file, title, poster, category, saveToDb, cancellationToken);
            if (added != null && TorrentHash.IsValid(added.Hash)) _discoveryMarkers?.ClearForManual(added.Hash);
            return added;
        }
        finally { _mutationGate.Release(); }
    }

    public async Task<bool> SetTorrentCategoryAsync(string hash, string category, string? title = null,
        string? poster = null, CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            if (TorrentHash.IsValid(hash)) _discoveryMarkers?.ClearForManual(hash);
            return await SetTorrentCategoryCoreAsync(hash, category, title, poster, cancellationToken);
        }
        finally { _mutationGate.Release(); }
    }

    public async Task<bool> RemoveTorrentAsync(string hash, CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            if (TorrentHash.IsValid(hash)) _discoveryMarkers?.ClearForManual(hash);
            return await RemoveTorrentCoreAsync(hash, cancellationToken);
        }
        finally { _mutationGate.Release(); }
    }

    internal async Task<bool> BackfillTorrentCategoryAsync(string hash, string category, CancellationToken ct)
    {
        if (!TorrentHash.IsValid(hash)) return false;
        await _mutationGate.WaitAsync(ct);
        try
        {
            // Automatic housekeeping is not manual adoption. Never revoke a lease or
            // overwrite a category set since the controller captured its original list.
            if (_discoveryMarkers?.Read(hash) != null) return false;
            var current = await GetTorrentAsync(hash, ct);
            if (current == null || !string.IsNullOrWhiteSpace(current.Category)) return false;
            return await SetTorrentCategoryCoreAsync(hash, category, current.Title, current.Poster, ct);
        }
        finally { _mutationGate.Release(); }
    }

    internal async Task<TorrTorrent?> UploadDiscoveryAsync(byte[] file, string hash, DiscoveryMarker attempt,
        string title, string? poster, CancellationToken ct)
    {
        await _mutationGate.WaitAsync(ct);
        try
        {
            var current = await ListTorrentsAsync(ct);
            if (_discoveryMarkers?.IsCurrent(hash, attempt.Nonce) != true || current == null || current.Any(t => TorrentHash.Equal(t.Hash, hash))) return null;
            var added = await AddTorrentFileCoreAsync(file, title, poster, attempt.OwnerCategory, true, ct);
            if (added == null || !TorrentHash.Equal(added.Hash, hash)) return null;
            // Some server versions may ignore multipart category. Assign it explicitly and
            // require a fresh readback before treating the server instance as ours.
            if (!await SetTorrentCategoryCoreAsync(hash, attempt.OwnerCategory, title, poster, ct)) return null;
            var stored = (await ListTorrentsAsync(ct))?.FirstOrDefault(t => TorrentHash.Equal(t.Hash, hash));
            return stored != null && attempt.Owns(stored) ? stored : null;
        }
        finally { _mutationGate.Release(); }
    }

    internal async Task FinalizeDiscoveryAsync(string hash, DiscoveryMarker attempt, string category, CancellationToken ct)
    {
        await _mutationGate.WaitAsync(ct);
        try
        {
            if (_discoveryMarkers?.IsCurrent(hash, attempt.Nonce) != true) return;
            var stored = (await ListTorrentsAsync(ct))?.FirstOrDefault(t => TorrentHash.Equal(t.Hash, hash));
            if (stored == null) return;
            // A foreign category is never overwritten. A committed STRM already proves that
            // this hash is safe to sync; only its old temporary category needs reconciliation.
            if (attempt.Owns(stored))
            {
                if (!await SetTorrentCategoryCoreAsync(hash, category, stored.Title, stored.Poster, ct)) return;
                stored = (await ListTorrentsAsync(ct))?.FirstOrDefault(t => TorrentHash.Equal(t.Hash, hash));
                if (stored == null || stored.Category != category) return;
            }
            _discoveryMarkers.Complete(hash, attempt.Nonce);
        }
        finally { _mutationGate.Release(); }
    }

    internal async Task CleanupDiscoveryAsync(string hash, DiscoveryMarker attempt, Func<bool> isReferenced, CancellationToken ct)
    {
        await _mutationGate.WaitAsync(ct);
        try
        {
            if (_discoveryMarkers?.IsCurrent(hash, attempt.Nonce) != true || isReferenced()) return;
            var current = await ListTorrentsAsync(ct);
            if (current == null) return;
            var stored = current.FirstOrDefault(t => TorrentHash.Equal(t.Hash, hash));
            // Unproven ownership keeps non-expiring suppression. Manual intake can adopt it.
            if (stored != null && (!attempt.Owns(stored) || !await RemoveTorrentCoreAsync(hash, ct))) return;
            _discoveryMarkers.Reject(hash, attempt.Nonce);
        }
        finally { _mutationGate.Release(); }
    }
}
