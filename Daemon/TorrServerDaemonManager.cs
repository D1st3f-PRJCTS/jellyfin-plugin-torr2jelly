using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Torr2Jelly.Configuration;
using Jellyfin.Plugin.Torr2Jelly.Web;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torr2Jelly.Daemon;

public class TorrServerDaemonManager : IHostedService, IDisposable
{
    private const int MaxVersionLength = 48;

    private readonly ILogger<TorrServerDaemonManager> _logger;
    private readonly HttpClient _httpClient;
    private readonly string? _webPath;

    // Serialises the read-modify-write of the TorrServer settings (watchdog cycle vs. a settings save).
    private readonly SemaphoreSlim _pushGate = new(1, 1);

    private CancellationTokenSource? _watchdogCts;
    private bool _isDisposed;
    private bool? _lastReachable;

    public bool IsRunning => !string.IsNullOrWhiteSpace(DaemonVersion) && DaemonVersion != "Unknown";
    public string StatusMessage { get; private set; } = "Connecting...";
    public string DaemonVersion { get; private set; } = "Unknown";

    public TorrServerDaemonManager(
        ILogger<TorrServerDaemonManager> logger,
        IHttpClientFactory httpClientFactory,
        IApplicationPaths applicationPaths)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("TorrDaemonClient");
        _httpClient.Timeout = TimeSpan.FromSeconds(5);

        _webPath = TryGetWebPath(applicationPaths);
    }

    // The host's own web client folder (the host knows it better than any list of well-known locations).
    private static string? TryGetWebPath(IApplicationPaths applicationPaths)
    {
        try
        {
            return applicationPaths.WebPath;
        }
        catch (Exception)
        {
            // an unexpected host version: fall back to the well-known locations
            return null;
        }
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

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Messages the plugin could not log while it was being constructed.
        Plugin.Instance?.AttachLogger(_logger);

        WebPatcher.TryPatchWebClient(_logger, _webPath);

        // The health check and the settings push talk to a remote service; they must never delay the host's startup.
        _watchdogCts = new CancellationTokenSource();
        var token = _watchdogCts.Token;
        _ = Task.Run(() => RunAsync(token), CancellationToken.None);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _watchdogCts?.Cancel();
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Torr2Jelly connecting to TorrServer at {Url}", RedactUrl(GetEffectiveUrl()));
            await CheckExternalServerAsync(cancellationToken);
            await EnsureCacheSettingsSyncedAsync(cancellationToken);
            await WatchdogLoopAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The TorrServer connection monitor stopped unexpectedly");
        }
    }

    /// <summary>
    /// Pushes the cache / seeding settings of the plugin to TorrServer. Does nothing unless the user opted in with
    /// <see cref="PluginConfiguration.ManageTorrServerSettings"/>: a TorrServer the plugin does not own keeps its settings.
    /// </summary>
    public async Task EnsureCacheSettingsSyncedAsync(CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!config.ManageTorrServerSettings)
        {
            return;
        }

        await _pushGate.WaitAsync(cancellationToken);
        try
        {
            await PushSettingsAsync(config, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check or sync TorrServer settings");
        }
        finally
        {
            _pushGate.Release();
        }
    }

    private async Task PushSettingsAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
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

        // Optional VPN port-forwarding file (only when the user configured one).
        var targetPort = await ReadForwardedPortAsync(config.ForwardedPortFilePath, cancellationToken);

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

        if (!needsUpdate)
        {
            return;
        }

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

    private static async Task<int?> ReadForwardedPortAsync(string? path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var trimmed = path.Trim();
            if (!Path.IsPathRooted(trimmed))
            {
                return null;
            }

            var info = new FileInfo(trimmed);
            if (!info.Exists || info.Length > 64)
            {
                return null;
            }

            var text = (await File.ReadAllTextAsync(info.FullName, cancellationToken)).Trim();
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535
                ? port
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // file locked or unreadable right now: keep the current port
            return null;
        }
    }

    public async Task ConfigureDaemonSettingsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureCacheSettingsSyncedAsync(cancellationToken);
    }

    public async Task<bool> CheckExternalServerAsync(CancellationToken cancellationToken = default)
    {
        var url = GetEffectiveUrl();
        try
        {
            using var response = await _httpClient.GetAsync($"{url}/echo", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var version = await ReadVersionAsync(response, cancellationToken);
            DaemonVersion = version;
            StatusMessage = $"Connected ({version})";
            ReportReachability(true, url, null);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            DaemonVersion = "Unknown";
            StatusMessage = $"Cannot reach TorrServer at {RedactUrl(url)}: {DescribeFailure(ex)}";
            ReportReachability(false, url, ex);
            return false;
        }
    }

    // The watchdog polls every 15 s; only a change of state is worth a log line.
    private void ReportReachability(bool reachable, string url, Exception? ex)
    {
        if (_lastReachable == reachable)
        {
            return;
        }

        _lastReachable = reachable;
        if (reachable)
        {
            _logger.LogInformation("TorrServer at {Url} is reachable ({Version})", RedactUrl(url), DaemonVersion);
        }
        else
        {
            _logger.LogWarning("TorrServer at {Url} is not reachable: {Reason}", RedactUrl(url), DescribeFailure(ex));
        }
    }

    // Reads at most a few hundred bytes of the /echo body: a mistyped URL may point at an arbitrary web page.
    private static async Task<string> ReadVersionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[256];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return SanitizeVersion(Encoding.UTF8.GetString(buffer, 0, total));
    }

    /// <summary>
    /// Reduces the body of TorrServer's <c>/echo</c> to a short, printable version string (e.g. <c>MatriX.136</c>).
    /// Bodies that look like HTML or JSON, or that contain nothing usable, yield <c>unspecified</c>.
    /// </summary>
    internal static string SanitizeVersion(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "unspecified";
        }

        var text = body.TrimStart();
        if (text.Length == 0 || text[0] == '<' || text[0] == '{' || text[0] == '[')
        {
            return "unspecified";
        }

        var line = text.Split('\r', '\n')[0];
        var sb = new StringBuilder(Math.Min(line.Length, MaxVersionLength));
        foreach (var ch in line)
        {
            if (sb.Length >= MaxVersionLength)
            {
                break;
            }

            if (char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' or '+' or ' ' or '(' or ')' or '/' or ':')
            {
                sb.Append(ch);
            }
        }

        var cleaned = sb.ToString().Trim();
        return cleaned.Length == 0 ? "unspecified" : cleaned;
    }

    private static string DescribeFailure(Exception? ex)
    {
        return ex switch
        {
            null => "unknown error",
            HttpRequestException { StatusCode: { } status } => $"HTTP {(int)status}",
            HttpRequestException => "connection failed",
            TaskCanceledException => "timed out",
            _ => ex.GetType().Name
        };
    }

    /// <summary>The URL without credentials, query or fragment, for logs and status messages.</summary>
    internal static string RedactUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return "(invalid URL)";
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        return $"{uri.Scheme}://{uri.Host}:{uri.Port}{path}";
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

                // Run cache settings sync every 60 seconds (every 4th cycle of 15s); a no-op unless the user opted in.
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
