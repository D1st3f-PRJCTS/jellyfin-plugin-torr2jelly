using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
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

public class TorrTorrentStat
{
    [JsonPropertyName("stat")]
    public int Stat { get; set; }

    [JsonPropertyName("stat_string")]
    public string StatString { get; set; } = string.Empty;

    [JsonPropertyName("loaded_size")]
    public long LoadedSize { get; set; }

    [JsonPropertyName("torrent_size")]
    public long TorrentSize { get; set; }

    [JsonPropertyName("download_speed")]
    public double DownloadSpeed { get; set; }

    [JsonPropertyName("upload_speed")]
    public double UploadSpeed { get; set; }

    [JsonPropertyName("connected_seeders")]
    public int ConnectedSeeders { get; set; }

    [JsonPropertyName("total_seeders")]
    public int TotalSeeders { get; set; }

    [JsonPropertyName("connected_peers")]
    public int ConnectedPeers { get; set; }

    [JsonPropertyName("total_peers")]
    public int TotalPeers { get; set; }

    [JsonPropertyName("active_peers")]
    public int ActivePeers { get; set; }
}

public class TorrTorrent
{
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

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

    [JsonPropertyName("file_stats")]
    public List<TorrTorrentFile>? FileStats { get; set; }
}

public class TorrClientService
{
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
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
        _daemonManager = daemonManager;
    }

    private string GetBaseUrl() => _daemonManager.GetEffectiveUrl();

    public async Task<List<TorrTorrent>> ListTorrentsAsync(CancellationToken cancellationToken = default)
    {
        var url = $"{GetBaseUrl()}/torrents";
        var payload = new { action = "list" };

        try
        {
            var response = await _httpClient.PostAsJsonAsync(url, payload, cancellationToken);
            response.EnsureSuccessStatusCode();

            var list = await response.Content.ReadFromJsonAsync<List<TorrTorrent>>(cancellationToken: cancellationToken);
            return list ?? new List<TorrTorrent>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list torrents from TorrServer at {Url}", url);
            return new List<TorrTorrent>();
        }
    }

    public async Task<TorrTorrent?> GetTorrentAsync(string hash, CancellationToken cancellationToken = default)
    {
        var url = $"{GetBaseUrl()}/torrents";
        var payload = new { action = "get", hash };

        try
        {
            var response = await _httpClient.PostAsJsonAsync(url, payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<TorrTorrent>(cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get torrent {Hash} from TorrServer", hash);
            return null;
        }
    }

    public async Task<TorrTorrent?> AddTorrentAsync(
        string link,
        string? title = null,
        string? poster = null,
        bool saveToDb = true,
        CancellationToken cancellationToken = default)
    {
        var url = $"{GetBaseUrl()}/torrents";
        var payload = new
        {
            action = "add",
            link,
            title = title ?? string.Empty,
            poster = poster ?? string.Empty,
            save_to_db = saveToDb
        };

        try
        {
            var response = await _httpClient.PostAsJsonAsync(url, payload, cancellationToken);
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadFromJsonAsync<TorrTorrent>(cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add torrent to TorrServer");
            throw;
        }
    }

    public async Task<bool> RemoveTorrentAsync(string hash, CancellationToken cancellationToken = default)
    {
        var url = $"{GetBaseUrl()}/torrents";
        var payload = new { action = "rem", hash };

        try
        {
            var response = await _httpClient.PostAsJsonAsync(url, payload, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove torrent {Hash}", hash);
            return false;
        }
    }

    public async Task<TorrTorrentStat?> GetTorrentStatsAsync(string hash, CancellationToken cancellationToken = default)
    {
        var url = $"{GetBaseUrl()}/torrents";
        var payload = new { action = "get", hash };

        try
        {
            var response = await _httpClient.PostAsJsonAsync(url, payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<TorrTorrentStat>(cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch stats for torrent {Hash}", hash);
            return null;
        }
    }
}
