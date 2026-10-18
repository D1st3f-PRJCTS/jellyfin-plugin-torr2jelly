using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.Torr2Jelly.Configuration;
using Jellyfin.Plugin.Torr2Jelly.Web;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Torr2Jelly;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public override string Name => "Torr2Jelly";

    public override Guid Id => Guid.Parse("b1a7d6e4-4f8a-4c9b-9d3e-7a1b2c3d4e5f");

    public override string Description => "Standalone TorrServer torrent streaming & Prowlarr/Torznab integration for Jellyfin";

    public static Plugin? Instance { get; private set; }

    private readonly IApplicationPaths _applicationPaths;
    private readonly object _configLock = new();

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _applicationPaths = applicationPaths;

        EnsureConfigurationDurable();
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
            try { Directory.CreateDirectory(dir); } catch { }
        }
        return Path.Combine(dir, "settings.backup.json");
    }

    private void EnsureConfigurationDurable()
    {
        lock (_configLock)
        {
            try
            {
                var xmlPath = ConfigurationFilePath;
                var backupPath1 = GetConfigBackupPath1();
                var backupPath2 = GetConfigBackupPath2();

                PluginConfiguration? restoredConfig = null;
                string? validBackup = null;

                if (File.Exists(backupPath1)) validBackup = backupPath1;
                else if (File.Exists(backupPath2)) validBackup = backupPath2;

                if (!string.IsNullOrEmpty(validBackup))
                {
                    try
                    {
                        var json = File.ReadAllText(validBackup);
                        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                        restoredConfig = JsonSerializer.Deserialize<PluginConfiguration>(json, opts);
                    }
                    catch
                    {
                        // Ignore corrupt backup
                    }
                }

                bool xmlExists = File.Exists(xmlPath);

                // If XML was missing/deleted (e.g. during plugin update or reinstall) and backup exists, restore it!
                if (!xmlExists && restoredConfig != null)
                {
                    Configuration = restoredConfig;
                    if (Configuration.PreloadCachePercent == 50) Configuration.PreloadCachePercent = 3;
                    if (Configuration.TorrentDisconnectTimeout <= 30) Configuration.TorrentDisconnectTimeout = 86400;
                    Configuration.DeduplicateCategories();
                    Configuration.NormalizeTrackerRules();
                    SaveConfiguration();
                    return;
                }

                // If XML exists and backup exists, check if XML was reset to bare defaults
                if (xmlExists && restoredConfig != null && Configuration != null)
                {
                    bool currentHasDefaultCats = Configuration.Categories == null || Configuration.Categories.Count == 0;
                    bool currentLacksProwlarr = string.IsNullOrWhiteSpace(Configuration.ProwlarrApiKey);
                    bool backupHasProwlarr = !string.IsNullOrWhiteSpace(restoredConfig.ProwlarrApiKey);
                    bool backupHasCustomCats = restoredConfig.Categories != null && restoredConfig.Categories.Count > 0;

                    if ((currentLacksProwlarr && backupHasProwlarr) || (currentHasDefaultCats && backupHasCustomCats))
                    {
                        Configuration = restoredConfig;
                        if (Configuration.PreloadCachePercent == 50) Configuration.PreloadCachePercent = 3;
                        if (Configuration.TorrentDisconnectTimeout <= 30) Configuration.TorrentDisconnectTimeout = 86400;
                        Configuration.DeduplicateCategories();
                        Configuration.NormalizeTrackerRules();
                        SaveConfiguration();
                        return;
                    }
                }

                if (Configuration != null)
                {
                    if (Configuration.PreloadCachePercent == 50) Configuration.PreloadCachePercent = 3;
                    if (Configuration.TorrentDisconnectTimeout <= 30) Configuration.TorrentDisconnectTimeout = 86400;
                }

                Configuration?.DeduplicateCategories();
                Configuration?.NormalizeTrackerRules();

                if (Configuration != null)
                {
                    SaveBackupConfig(Configuration);
                }
            }
            catch
            {
                // Fallback safe handling
                Configuration?.DeduplicateCategories();
                Configuration?.NormalizeTrackerRules();
            }
        }
    }

    public void SaveBackupConfig(PluginConfiguration? config)
    {
        if (config == null) return;
        lock (_configLock)
        {
            try
            {
                var opts = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(config, opts);

                var p1 = GetConfigBackupPath1();
                var d1 = Path.GetDirectoryName(p1);
                if (!string.IsNullOrEmpty(d1) && !Directory.Exists(d1)) Directory.CreateDirectory(d1);
                File.WriteAllText(p1, json);

                var p2 = GetConfigBackupPath2();
                var d2 = Path.GetDirectoryName(p2);
                if (!string.IsNullOrEmpty(d2) && !Directory.Exists(d2)) Directory.CreateDirectory(d2);
                File.WriteAllText(p2, json);
            }
            catch
            {
                // Silent catch
            }
        }
    }

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is PluginConfiguration pc) { pc.DeduplicateCategories(); pc.NormalizeTrackerRules(); }
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
                Name = "torr2jellyjs",
                EmbeddedResourcePath = $"{GetType().Namespace}.Web.torr2jelly.js"
            },
            new PluginPageInfo
            {
                Name = "torr2jellycss",
                EmbeddedResourcePath = $"{GetType().Namespace}.Web.torr2jelly.css"
            },
            new PluginPageInfo
            {
                Name = "torr2jellysearch",
                EmbeddedResourcePath = $"{GetType().Namespace}.Web.torr2jellysearch.js"
            }
        };
    }
}
