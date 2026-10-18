using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Torr2Jelly.Configuration;
using Jellyfin.Plugin.Torr2Jelly.Engine;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torr2Jelly.Tasks;

public class TorrSyncScheduledTask : IScheduledTask
{
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

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerInterval,
                IntervalTicks = TimeSpan.FromMinutes(15).Ticks
            }
        };
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
            var torrents = await _torrClient.ListTorrentsAsync(cancellationToken);
            if (torrents.Count == 0)
            {
                _logger.LogInformation("No active torrents found in TorrServer.");
                progress?.Report(100);
                return;
            }

            var index = 0;
            foreach (var torrent in torrents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _strmEngine.SyncTorrentAsync(torrent, cancellationToken: cancellationToken);
                index++;
                progress?.Report((double)index / torrents.Count * 100);
            }

            _logger.LogInformation("Torr2Jelly scheduled sync completed. Synced {Count} torrents.", torrents.Count);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Torr2Jelly scheduled sync was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during Torr2Jelly scheduled sync");
        }
        finally
        {
            progress?.Report(100);
        }
    }
}
