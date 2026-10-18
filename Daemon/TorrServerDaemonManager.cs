using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
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
        await EnsureCacheSettingsSyncedAsync(cancellationToken);

        _watchdogCts = new CancellationTokenSource();
        _ = Task.Run(() => WatchdogLoopAsync(_watchdogCts.Token), CancellationToken.None);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _watchdogCts?.Cancel();
        return Task.CompletedTask;
    }

    public async Task EnsureCacheSettingsSyncedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var effectiveUrl = GetEffectiveUrl();

            // 1. Fetch current settings from TorrServer
            using var getResponse = await _httpClient.PostAsJsonAsync(
                $"{effectiveUrl}/settings",
                new { action = "get" },
                cancellationToken);

            if (!getResponse.IsSuccessStatusCode)
            {
                return;
            }

            var jsonContent = await getResponse.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(jsonContent))
            {
                return;
            }

            var rootObj = JsonNode.Parse(jsonContent)?.AsObject();
            if (rootObj == null)
            {
                return;
            }

            var desiredCacheBytes = (long)config.CacheSizeMb * 1024 * 1024;
            var currentUseDisk = rootObj["UseDisk"]?.GetValue<bool>() ?? false;
            var currentCacheSize = rootObj["CacheSize"]?.GetValue<long>() ?? 0;
            var currentPreload = rootObj["PreloadCache"]?.GetValue<int>() ?? 0;
            var currentReader = rootObj["ReaderReadAHead"]?.GetValue<int>() ?? 0;
            var currentPath = rootObj["TorrentsSavePath"]?.GetValue<string>() ?? string.Empty;
            var currentPort = rootObj["PeersListenPort"]?.GetValue<int>() ?? 0;
            var currentDisconnect = rootObj["TorrentDisconnectTimeout"]?.GetValue<int>() ?? 0;
            var currentRemoveOnDrop = rootObj["RemoveCacheOnDrop"]?.GetValue<bool>() ?? true;

            // Check if Proton VPN forwarded port file exists
            int? targetPort = null;
            string[] possiblePortFiles = { "/mnt/sdb/AppFiles/torrserver/data/forwarded_port", "/opt/ts/forwarded_port" };
            foreach (var pf in possiblePortFiles)
            {
                if (File.Exists(pf))
                {
                    try
                    {
                        var portStr = (await File.ReadAllTextAsync(pf, cancellationToken)).Trim();
                        if (int.TryParse(portStr, out var p) && p > 0)
                        {
                            targetPort = p;
                            break;
                        }
                    }
                    catch
                    {
                        // Ignore file read locks
                    }
                }
            }

            var needsUpdate = false;

            if (currentUseDisk != config.UseDiskCache ||
                currentCacheSize != desiredCacheBytes ||
                currentPreload != config.PreloadCachePercent ||
                currentReader != config.ReaderReadAHeadPercent ||
                currentPath != (config.DiskCachePath ?? string.Empty) ||
                currentDisconnect != config.TorrentDisconnectTimeout ||
                currentRemoveOnDrop != false)
            {
                needsUpdate = true;
                _logger.LogInformation(
                    "TorrServer cache/seeding mismatch detected (Current: Disk={CurDisk}, Size={CurMb}MB, Preload={CurPreload}%, Reader={CurReader}%, Timeout={CurTimeout}s, RemoveOnDrop={CurDrop}; Desired: Disk={DesDisk}, Size={DesMb}MB, Preload={DesPreload}%, Reader={DesReader}%, Timeout={DesTimeout}s, RemoveOnDrop=false). Syncing...",
                    currentUseDisk, currentCacheSize / (1024 * 1024), currentPreload, currentReader, currentDisconnect, currentRemoveOnDrop,
                    config.UseDiskCache, config.CacheSizeMb, config.PreloadCachePercent, config.ReaderReadAHeadPercent, config.TorrentDisconnectTimeout);
            }

            if (targetPort.HasValue && currentPort != targetPort.Value)
            {
                needsUpdate = true;
                _logger.LogInformation("TorrServer peer port mismatch detected (Current: {CurPort}, Target: {TgtPort}). Syncing...",
                    currentPort, targetPort.Value);
            }

            if (needsUpdate)
            {
                rootObj["UseDisk"] = config.UseDiskCache;
                rootObj["CacheSize"] = desiredCacheBytes;
                rootObj["PreloadCache"] = config.PreloadCachePercent;
                rootObj["ReaderReadAHead"] = config.ReaderReadAHeadPercent;
                rootObj["TorrentsSavePath"] = config.DiskCachePath ?? string.Empty;
                rootObj["TorrentDisconnectTimeout"] = config.TorrentDisconnectTimeout;
                rootObj["RemoveCacheOnDrop"] = false;

                if (targetPort.HasValue)
                {
                    rootObj["PeersListenPort"] = targetPort.Value;
                }

                var payload = new
                {
                    action = "set",
                    sets = rootObj
                };

                using var setResponse = await _httpClient.PostAsJsonAsync(
                    $"{effectiveUrl}/settings",
                    payload,
                    cancellationToken);

                if (setResponse.IsSuccessStatusCode)
                {
                    _logger.LogInformation(
                        "TorrServer settings synced successfully: {Mb} MB (Disk: {UseDisk}, Path: '{Path}', Port: {Port}, Preload: {Preload}%, Reader: {Reader}%, SeedingTimeout: {Timeout}s, RemoveCacheOnDrop: false)",
                        config.CacheSizeMb, config.UseDiskCache, config.DiskCachePath, rootObj["PeersListenPort"]?.GetValue<int>(),
                        config.PreloadCachePercent, config.ReaderReadAHeadPercent, config.TorrentDisconnectTimeout);
                }
                else
                {
                    _logger.LogWarning("Failed to apply TorrServer settings, status code: {Code}", setResponse.StatusCode);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check or sync TorrServer settings");
        }
    }

    public async Task ConfigureDaemonSettingsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureCacheSettingsSyncedAsync(cancellationToken);
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
        var syncCycleCounter = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
                var isOnline = await CheckExternalServerAsync(cancellationToken);

                // Run cache settings sync every 60 seconds (every 4th cycle of 15s)
                syncCycleCounter++;
                if (isOnline && syncCycleCounter >= 4)
                {
                    syncCycleCounter = 0;
                    await EnsureCacheSettingsSyncedAsync(cancellationToken);
                }
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
        await EnsureCacheSettingsSyncedAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _watchdogCts?.Cancel();
        _watchdogCts?.Dispose();
    }
}
