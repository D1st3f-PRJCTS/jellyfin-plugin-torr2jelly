using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Torr2Jelly.Configuration;
using Jellyfin.Plugin.Torr2Jelly.Engine;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torr2Jelly.Tasks;

public class TorrSyncScheduledTask : IScheduledTask
{
    private const int MinIntervalMinutes = 5;

    private readonly ILogger<TorrSyncScheduledTask> _logger;
    private readonly TorrClientService _torrClient;
    private readonly StrmEngine _strmEngine;

    public string Name => "Torr2Jelly Library Sync";

    public string Key => "Torr2JellySyncTask";

    public string Description => "Periodically synchronizes active TorrServer torrents into Jellyfin STRM library";

    public string Category => "Library";

    public TorrSyncScheduledTask(
        ILogger<TorrSyncScheduledTask> logger,
        TorrClientService torrClient,
        StrmEngine strmEngine)
    {
        _logger = logger;
        _torrClient = torrClient;
        _strmEngine = strmEngine;
    }

    /// <summary>
    /// Default trigger: every <c>AutoSyncIntervalMinutes</c> (at least 5). Jellyfin only asks for the defaults when
    /// the task has no stored triggers, so a later change of the setting does not move an existing trigger.
    /// </summary>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var minutes = Math.Max(MinIntervalMinutes, config.AutoSyncIntervalMinutes);

        var trigger = new TaskTriggerInfo
        {
            IntervalTicks = TimeSpan.FromMinutes(minutes).Ticks
        };

        // TaskTriggerInfo.Type is a string up to Jellyfin 10.10 and an enum (TaskTriggerInfoType) from 10.11 on, and
        // the TriggerInterval constant is gone there. Assigning it by reflection keeps one binary working on all of them.
        if (!TrySetTriggerType(trigger, "IntervalTrigger"))
        {
            _logger.LogWarning("Could not set the trigger type of the Torr2Jelly sync task; it has no default schedule");
            return Array.Empty<TaskTriggerInfo>();
        }

        return new[] { trigger };
    }

    /// <summary>
    /// Sets the <c>Type</c> property of a task trigger to <paramref name="typeName"/>, whether the property is a
    /// string or an enum. Returns false when the property is missing or has another type.
    /// </summary>
    internal static bool TrySetTriggerType(object trigger, string typeName)
    {
        var property = trigger.GetType().GetProperty("Type", BindingFlags.Public | BindingFlags.Instance);
        if (property == null || !property.CanWrite)
        {
            return false;
        }

        var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (propertyType == typeof(string))
        {
            property.SetValue(trigger, typeName);
            return true;
        }

        if (propertyType.IsEnum && Enum.TryParse(propertyType, typeName, ignoreCase: true, out var value))
        {
            property.SetValue(trigger, value);
            return true;
        }

        return false;
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!config.AutoSyncTorrents)
        {
            _logger.LogInformation("Torr2Jelly AutoSync is disabled. Skipping task.");
            return;
        }

        _logger.LogInformation("Starting Torr2Jelly scheduled sync...");
        progress?.Report(0);

        try
        {
            // Orphans are never purged by the scheduled run: that is left to the explicit Library/Scan.
            var report = await _strmEngine.FullSyncFromServerAsync(_torrClient, purgeOrphans: false, cancellationToken, progress);

            _logger.LogInformation(
                "Torr2Jelly scheduled sync completed: {Synced} synced, {Failed} failed, {NotReady} waiting for metadata.",
                report.Synced, report.Failed, report.SkippedNotReady);

            if (report.Failed > 0)
            {
                _logger.LogWarning(
                    "Torr2Jelly scheduled sync had {Failed} failure(s): {Errors}",
                    report.Failed,
                    string.Join("; ", report.Errors.Take(5)));
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Torr2Jelly scheduled sync was cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during Torr2Jelly scheduled sync");
            throw;
        }
        finally
        {
            progress?.Report(100);
        }
    }
}
