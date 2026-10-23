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
    public async Task<TorrTorrent?> AddTorrentAsync(
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

    public async Task<bool> SetTorrentCategoryAsync(
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

    public async Task<bool> RemoveTorrentAsync(string hash, CancellationToken cancellationToken = default)
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
}
