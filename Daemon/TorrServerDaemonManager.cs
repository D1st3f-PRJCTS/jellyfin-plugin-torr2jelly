using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Torr2Jelly.Configuration;
using Jellyfin.Plugin.Torr2Jelly.Web;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torr2Jelly.Daemon;

public class TorrServerDaemonManager : IHostedService, IDisposable
{
    private readonly ILogger<TorrServerDaemonManager> _logger;
    private readonly HttpClient _httpClient;
    private CancellationTokenSource? _watchdogCts;
    private bool _isDisposed;

    public bool IsRunning => !string.IsNullOrWhiteSpace(DaemonVersion) && DaemonVersion != "Unknown";
    public string StatusMessage { get; private set; } = "Connecting...";
    public string DaemonVersion { get; private set; } = "Unknown";

    public TorrServerDaemonManager(
        ILogger<TorrServerDaemonManager> logger,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("TorrDaemonClient");
        _httpClient.Timeout = TimeSpan.FromSeconds(5);
    }

    public string GetEffectiveUrl()
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        return string.IsNullOrWhiteSpace(config.ExternalTorrServerUrl)
            ? "http://torrserver:8090"
            : config.ExternalTorrServerUrl.TrimEnd('/');
    }

    public string GetStreamBaseUrl()
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!string.IsNullOrWhiteSpace(config.TorrServerStreamUrl))
        {
            return config.TorrServerStreamUrl.TrimEnd('/');
        }

        return GetEffectiveUrl();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        WebPatcher.TryPatchWebClient(_logger);
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        _logger.LogInformation("Torr2Jelly connecting to TorrServer at {Url}", config.ExternalTorrServerUrl);
        await CheckExternalServerAsync(cancellationToken);

        _watchdogCts = new CancellationTokenSource();
        _ = Task.Run(() => WatchdogLoopAsync(_watchdogCts.Token), CancellationToken.None);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _watchdogCts?.Cancel();
        return Task.CompletedTask;
    }

    public async Task ConfigureDaemonSettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var effectiveUrl = GetEffectiveUrl();

            var cacheSizeBytes = (long)config.CacheSizeMb * 1024 * 1024;
            var payload = new
            {
                action = "set",
                sets = new
                {
                    CacheSize = cacheSizeBytes,
                    PreloadCache = config.PreloadCachePercent,
                    ReaderReadAHead = config.ReaderReadAHeadPercent,
                    UseDisk = config.UseDiskCache,
                    TorrentsSavePath = config.DiskCachePath ?? string.Empty
                }
            };

            var response = await _httpClient.PostAsJsonAsync($"{effectiveUrl}/settings", payload, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("TorrServer cache configured: {Mb} MB RAM, Preload: {Preload}%", config.CacheSizeMb, config.PreloadCachePercent);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply TorrServer settings");
        }
    }

    public async Task<bool> CheckExternalServerAsync(CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        try
        {
            var url = GetEffectiveUrl();
            var response = await _httpClient.GetStringAsync($"{url}/echo", cancellationToken);
            DaemonVersion = response.Trim();
            StatusMessage = $"Connected ({DaemonVersion})";
            return true;
        }
        catch (Exception ex)
        {
            DaemonVersion = "Unknown";
            StatusMessage = $"Cannot reach {config.ExternalTorrServerUrl}: {ex.Message}";
            return false;
        }
    }

    private async Task WatchdogLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
                await CheckExternalServerAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error in TorrServer health check loop");
            }
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await CheckExternalServerAsync(cancellationToken);
        await ConfigureDaemonSettingsAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _watchdogCts?.Cancel();
        _watchdogCts?.Dispose();
    }
}
