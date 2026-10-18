using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Torr2Jelly.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torr2Jelly.Daemon;

public class TorrServerDaemonManager : IHostedService, IDisposable
{
    private readonly ILogger<TorrServerDaemonManager> _logger;
    private readonly IApplicationPaths _appPaths;
    private readonly HttpClient _httpClient;
    private Process? _process;
    private CancellationTokenSource? _watchdogCts;
    private bool _isDisposed;

    public bool IsRunning => _process != null && !_process.HasExited;
    public int? ProcessId => IsRunning ? _process?.Id : null;
    public string StatusMessage { get; private set; } = "Stopped";
    public string DaemonVersion { get; private set; } = "Unknown";
    public DateTime? StartedAt { get; private set; }

    public TorrServerDaemonManager(
        ILogger<TorrServerDaemonManager> logger,
        IApplicationPaths appPaths,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _appPaths = appPaths;
        _httpClient = httpClientFactory.CreateClient("TorrDaemonClient");
        _httpClient.Timeout = TimeSpan.FromSeconds(5);
    }

    public string GetBinDirectory()
    {
        var dir = Path.Combine(_appPaths.PluginConfigurationsPath, "Torr2Jelly", "bin");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string GetDataDirectory()
    {
        var dir = Path.Combine(_appPaths.PluginConfigurationsPath, "Torr2Jelly", "torrdata");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string GetLogFilePath()
    {
        var dir = Path.Combine(_appPaths.LogDirectoryPath, "torrserver.log");
        return dir;
    }

    public string GetBinaryPath()
    {
        return Path.Combine(GetBinDirectory(), PlatformResolver.GetTargetExecutableName());
    }

    public string GetEffectiveUrl()
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (config.UseEmbeddedTorrServer)
        {
            return $"http://127.0.0.1:{config.EmbeddedTorrServerPort}";
        }

        return string.IsNullOrWhiteSpace(config.ExternalTorrServerUrl)
            ? "http://127.0.0.1:8090"
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
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!config.UseEmbeddedTorrServer)
        {
            StatusMessage = "Using External TorrServer";
            _logger.LogInformation("Torr2Jelly is configured to use external TorrServer at {Url}", config.ExternalTorrServerUrl);
            await CheckExternalServerAsync(cancellationToken);
            return;
        }

        try
        {
            await EnsureBinaryExistsAsync(cancellationToken);
            await StartProcessAsync(cancellationToken);

            _watchdogCts = new CancellationTokenSource();
            _ = Task.Run(() => WatchdogLoopAsync(_watchdogCts.Token), CancellationToken.None);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to start: {ex.Message}";
            _logger.LogError(ex, "Failed to start embedded TorrServer daemon");
        }
    }

    public async Task EnsureBinaryExistsAsync(CancellationToken cancellationToken)
    {
        var binPath = GetBinaryPath();
        if (File.Exists(binPath) && new FileInfo(binPath).Length > 1024 * 1024)
        {
            EnsureExecutablePermissions(binPath);
            return;
        }

        var binaryName = PlatformResolver.GetBinaryName();
        var downloadUrl = PlatformResolver.GetDownloadUrl(binaryName);

        StatusMessage = $"Downloading TorrServer ({binaryName})...";
        _logger.LogInformation("Downloading TorrServer from {Url} to {Path}...", downloadUrl, binPath);

        var tempPath = binPath + ".tmp";
        using (var downloadClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        using (var response = await downloadClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await response.Content.CopyToAsync(fileStream, cancellationToken);
        }

        if (File.Exists(binPath))
        {
            File.Delete(binPath);
        }

        File.Move(tempPath, binPath);
        EnsureExecutablePermissions(binPath);
        _logger.LogInformation("TorrServer binary successfully installed to {Path}", binPath);
    }

    private void EnsureExecutablePermissions(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            try
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to set unix file mode via .NET API: {Message}. Trying chmod...", ex.Message);
                try
                {
                    Process.Start("chmod", $"+x \"{path}\"")?.WaitForExit(3000);
                }
                catch (Exception chmodEx)
                {
                    _logger.LogWarning(chmodEx, "chmod +x failed");
                }
            }
        }
    }

    private async Task StartProcessAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var binPath = GetBinaryPath();
        var dataDir = GetDataDirectory();
        var logFile = GetLogFilePath();
        var port = config.EmbeddedTorrServerPort;

        if (IsRunning)
        {
            StopProcess();
        }

        StatusMessage = "Starting process...";
        _logger.LogInformation("Launching embedded TorrServer on port {Port}...", port);

        var startInfo = new ProcessStartInfo
        {
            FileName = binPath,
            Arguments = $"-d \"{dataDir}\" -p {port} -l \"{logFile}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = GetBinDirectory(),
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.Exited += OnProcessExited;

        if (!_process.Start())
        {
            throw new InvalidOperationException("Failed to start TorrServer process.");
        }

        StartedAt = DateTime.UtcNow;

        // Wait for server to become responsive
        var isHealthy = false;
        var checkUrl = $"http://127.0.0.1:{port}/echo";
        for (var i = 0; i < 20; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var response = await _httpClient.GetStringAsync(checkUrl, cancellationToken);
                if (!string.IsNullOrWhiteSpace(response))
                {
                    DaemonVersion = response.Trim();
                    isHealthy = true;
                    break;
                }
            }
            catch
            {
                await Task.Delay(500, cancellationToken);
            }
        }

        if (!isHealthy)
        {
            StatusMessage = "Process started but healthcheck failed";
            _logger.LogWarning("TorrServer started with PID {Pid}, but health check timed out", _process.Id);
            return;
        }

        StatusMessage = "Running";
        _logger.LogInformation("TorrServer is running with PID {Pid}, version: {Version}", _process.Id, DaemonVersion);

        // Configure cache and buffer settings
        await ConfigureDaemonSettingsAsync(cancellationToken);
    }

    public async Task ConfigureDaemonSettingsAsync(CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var effectiveUrl = GetEffectiveUrl();

        try
        {
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
                    TorrentsSavePath = string.IsNullOrWhiteSpace(config.DiskCachePath) ? GetDataDirectory() : config.DiskCachePath
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

    private async Task CheckExternalServerAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        try
        {
            var response = await _httpClient.GetStringAsync($"{config.ExternalTorrServerUrl.TrimEnd('/')}/echo", cancellationToken);
            DaemonVersion = response.Trim();
            StatusMessage = $"Connected ({DaemonVersion})";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Cannot reach {config.ExternalTorrServerUrl}: {ex.Message}";
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var exitCode = _process?.ExitCode;
        _logger.LogWarning("TorrServer process exited with code {ExitCode}", exitCode);
        StatusMessage = $"Exited with code {exitCode}";
    }

    private async Task WatchdogLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);

                var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
                if (!config.UseEmbeddedTorrServer)
                {
                    continue;
                }

                if (!IsRunning)
                {
                    _logger.LogWarning("Watchdog: TorrServer is not running! Attempting restart...");
                    await StartProcessAsync(cancellationToken);
                }
                else
                {
                    // Ping healthcheck
                    try
                    {
                        var port = config.EmbeddedTorrServerPort;
                        var res = await _httpClient.GetStringAsync($"http://127.0.0.1:{port}/echo", cancellationToken);
                        if (!string.IsNullOrWhiteSpace(res))
                        {
                            StatusMessage = "Running";
                        }
                    }
                    catch
                    {
                        StatusMessage = "Unresponsive";
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in TorrServer watchdog loop");
            }
        }
    }

    public void StopProcess()
    {
        try
        {
            if (_process != null && !_process.HasExited)
            {
                _logger.LogInformation("Stopping TorrServer process (PID {Pid})...", _process.Id);
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3000);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error terminating TorrServer process");
        }
        finally
        {
            _process?.Dispose();
            _process = null;
            StatusMessage = "Stopped";
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _watchdogCts?.Cancel();
        StopProcess();
        return Task.CompletedTask;
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        StopProcess();
        await StartAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _watchdogCts?.Cancel();
        _watchdogCts?.Dispose();
        StopProcess();
        _httpClient.Dispose();
    }
}
