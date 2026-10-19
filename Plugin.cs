using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.Torr2Jelly.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torr2Jelly;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    // Port files of the original author's setup. Only the migration of an existing installation looks at them.
    private static readonly string[] LegacyForwardedPortFiles =
    {
        "/mnt/sdb/AppFiles/torrserver/data/forwarded_port",
        "/opt/ts/forwarded_port"
    };

    private const int MaxDeferredMessages = 50;

    private static readonly JsonSerializerOptions BackupWriteOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions BackupReadOptions = new() { PropertyNameCaseInsensitive = true };

    public override string Name => "Torr2Jelly";

    public override Guid Id => Guid.Parse("b1a7d6e4-4f8a-4c9b-9d3e-7a1b2c3d4e5f");

    public override string Description => "Standalone TorrServer torrent streaming & Prowlarr/Torznab integration for Jellyfin";

    public static Plugin? Instance { get; private set; }

    private readonly IApplicationPaths _applicationPaths;
    private readonly object _configLock = new();

    // The plugin is constructed before the logging infrastructure is available, so messages produced while
    // loading the configuration are queued and written as soon as a logger is attached (see AttachLogger).
    private readonly ConcurrentQueue<(LogLevel Level, string Message, Exception? Error)> _deferredLogs = new();
    private ILogger? _logger;

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _applicationPaths = applicationPaths;

        // BasePlugin loads the configuration lazily on first access and writes a default file when there is none.
        // Whether the file existed has to be known before anything touches Configuration.
        var configFileExisted = File.Exists(ConfigurationFilePath);

        EnsureConfigurationDurable(configFileExisted);
    }

    /// <summary>Hands over a logger; everything queued while the plugin was being constructed is written to it.</summary>
    public void AttachLogger(ILogger logger)
    {
        _logger = logger;
        while (_deferredLogs.TryDequeue(out var entry))
        {
            logger.Log(entry.Level, entry.Error, "{Message}", entry.Message);
        }
    }

    private void Log(LogLevel level, string message, Exception? error = null)
    {
        var logger = _logger;
        if (logger != null)
        {
            logger.Log(level, error, "{Message}", message);
            return;
        }

        if (_deferredLogs.Count < MaxDeferredMessages)
        {
            _deferredLogs.Enqueue((level, message, error));
        }
    }

    private string GetConfigBackupPath1()
    {
        return Path.Combine(_applicationPaths.PluginConfigurationsPath, "Jellyfin.Plugin.Torr2Jelly.backup.json");
    }

    private string GetConfigBackupPath2()
    {
        var dir = Path.Combine(_applicationPaths.DataPath, "torr2jelly");
        if (!Directory.Exists(dir))
        {
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                Log(LogLevel.Warning, $"Could not create the Torr2Jelly data folder '{dir}'", ex);
            }
        }
        return Path.Combine(dir, "settings.backup.json");
    }

    private PluginConfiguration? TryReadBackup()
    {
        foreach (var path in new[] { GetConfigBackupPath1(), GetConfigBackupPath2() })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var restored = JsonSerializer.Deserialize<PluginConfiguration>(File.ReadAllText(path), BackupReadOptions);
                if (restored != null)
                {
                    return restored;
                }
            }
            catch (Exception ex)
            {
                Log(LogLevel.Warning, $"Ignoring the unreadable configuration backup '{path}'", ex);
            }
        }

        return null;
    }

    private void EnsureConfigurationDurable(bool configFileExisted)
    {
        lock (_configLock)
        {
            try
            {
                var backup = TryReadBackup();
                var restoredFromBackup = false;

                if (!configFileExisted && backup != null)
                {
                    // The XML is missing (plugin reinstalled, file deleted) but a backup exists: bring it back.
                    Configuration = backup;
                    restoredFromBackup = true;
                    Log(LogLevel.Information, "Torr2Jelly configuration restored from its backup");
                }
                else if (configFileExisted && backup != null &&
                         Configuration.ConfigVersion < PluginConfiguration.CurrentConfigVersion &&
                         LooksResetToDefaults(Configuration, backup))
                {
                    // The XML was reset to bare defaults while the backup still holds the real settings.
                    // Only before the migration has run: afterwards a cleared field is the user's decision.
                    Configuration = backup;
                    restoredFromBackup = true;
                    Log(LogLevel.Information, "Torr2Jelly configuration looked reset to defaults; restored from its backup");
                }

                var config = Configuration;
                var migrated = false;

                if (config.ConfigVersion < PluginConfiguration.CurrentConfigVersion)
                {
                    // A restored backup comes from an earlier installation as well.
                    MigrateToCurrentVersion(config, existingInstall: configFileExisted || restoredFromBackup);
                    migrated = true;
                }

                config.DeduplicateCategories();
                config.NormalizeTrackerRules();

                if (migrated || restoredFromBackup)
                {
                    // Writes the XML and both backups.
                    SaveConfiguration();
                }
                else
                {
                    SaveBackupConfig(config);
                }
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, "Checking the Torr2Jelly configuration failed", ex);

                try
                {
                    Configuration?.DeduplicateCategories();
                    Configuration?.NormalizeTrackerRules();
                }
                catch (Exception inner)
                {
                    Log(LogLevel.Error, "Normalizing the Torr2Jelly configuration failed", inner);
                }
            }
        }
    }

    private static bool LooksResetToDefaults(PluginConfiguration current, PluginConfiguration backup)
    {
        var currentHasDefaultCats = current.Categories == null || current.Categories.Count == 0;
        var currentLacksProwlarr = string.IsNullOrWhiteSpace(current.ProwlarrApiKey);
        var backupHasProwlarr = !string.IsNullOrWhiteSpace(backup.ProwlarrApiKey);
        var backupHasCustomCats = backup.Categories != null && backup.Categories.Count > 0;

        return (currentLacksProwlarr && backupHasProwlarr) || (currentHasDefaultCats && backupHasCustomCats);
    }

    /// <summary>
    /// One-time upgrade of a stored configuration to <see cref="PluginConfiguration.CurrentConfigVersion"/>.
    /// A fresh installation keeps the new defaults; an existing one keeps the behaviour it had before.
    /// </summary>
    private void MigrateToCurrentVersion(PluginConfiguration config, bool existingInstall)
    {
        // Values older releases silently forced on every start. They are corrected once here, so a user who
        // deliberately chooses one of them later keeps it.
        if (config.PreloadCachePercent == 50) config.PreloadCachePercent = 3;
        if (config.TorrentDisconnectTimeout <= 30) config.TorrentDisconnectTimeout = 86400;

        if (existingInstall)
        {
            // Earlier releases pushed their cache settings to TorrServer unconditionally (and read the port files
            // of the original author's setup). That stays the case for installations that already ran like this.
            config.ManageTorrServerSettings = true;

            if (string.IsNullOrWhiteSpace(config.ForwardedPortFilePath))
            {
                config.ForwardedPortFilePath = FindLegacyForwardedPortFile() ?? string.Empty;
            }

            // Without stored categories, earlier releases fell back to absolute default folders. Keep those folders:
            // the .strm files already written there belong to the library.
            if (config.Categories == null || config.Categories.Count == 0)
            {
                config.Categories = PluginConfiguration.GetLegacyDefaultCategories();
            }
        }

        config.ConfigVersion = PluginConfiguration.CurrentConfigVersion;
        Log(
            LogLevel.Information,
            $"Torr2Jelly configuration migrated to version {PluginConfiguration.CurrentConfigVersion} ({(existingInstall ? "existing" : "new")} installation; manage TorrServer settings: {config.ManageTorrServerSettings})");
    }

    private static string? FindLegacyForwardedPortFile()
    {
        foreach (var path in LegacyForwardedPortFiles)
        {
            try
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }
            catch (Exception)
            {
                // an unreadable location counts as absent
            }
        }

        return null;
    }

    public void SaveBackupConfig(PluginConfiguration? config)
    {
        if (config == null) return;
        lock (_configLock)
        {
            try
            {
                var json = JsonSerializer.Serialize(config, BackupWriteOptions);

                var p1 = GetConfigBackupPath1();
                var d1 = Path.GetDirectoryName(p1);
                if (!string.IsNullOrEmpty(d1) && !Directory.Exists(d1)) Directory.CreateDirectory(d1);
                WriteAllTextAtomic(p1, json);

                var p2 = GetConfigBackupPath2();
                var d2 = Path.GetDirectoryName(p2);
                if (!string.IsNullOrEmpty(d2) && !Directory.Exists(d2)) Directory.CreateDirectory(d2);
                WriteAllTextAtomic(p2, json);
            }
            catch (Exception ex)
            {
                Log(LogLevel.Warning, "Could not write the Torr2Jelly configuration backup", ex);
            }
        }
    }

    // A crash between truncate and write must never leave a half-written backup behind.
    private static void WriteAllTextAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is PluginConfiguration pc)
        {
            pc.DeduplicateCategories();
            pc.NormalizeTrackerRules();

            // A client that replaces the whole configuration without knowing ConfigVersion must not make the
            // next start run the migration again.
            var current = Configuration.ConfigVersion;
            if (pc.ConfigVersion < current)
            {
                pc.ConfigVersion = current;
            }
        }

        base.UpdateConfiguration(configuration);
        if (configuration is PluginConfiguration pcfg) SaveBackupConfig(pcfg);
    }

    public override void SaveConfiguration()
    {
        base.SaveConfiguration();
        SaveBackupConfig(Configuration);
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "torr2jelly",
                EmbeddedResourcePath = $"{GetType().Namespace}.Web.torr2jelly.html",
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "cloud_download",
                DisplayName = "Torr2Jelly"
            },
            new PluginPageInfo
            {
                Name = "torr2jellysearch",
                EmbeddedResourcePath = $"{GetType().Namespace}.Web.torr2jellysearch.js"
            }
        };
    }
}
