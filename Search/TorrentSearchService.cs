using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Torr2Jelly.Configuration;
using Jellyfin.Plugin.Torr2Jelly.Search.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torr2Jelly.Search;

public class TorrentSearchService
{
    private readonly ILogger<TorrentSearchService> _logger;
    private readonly ProwlarrSearchProvider _prowlarr;
    private readonly TorznabSearchProvider _torznab;

    public TorrentSearchService(
        ILogger<TorrentSearchService> logger,
        ProwlarrSearchProvider prowlarr,
        TorznabSearchProvider torznab)
    {
        _logger = logger;
        _prowlarr = prowlarr;
        _torznab = torznab;
    }

    public async Task<List<TorrentSearchResult>> SearchAsync(
        string query,
        string? category = null,
        bool isUserAdmin = true,
        CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var results = new List<TorrentSearchResult>();

        // Determine category IDs
        List<int>? categoryIds = null;
        if (!string.IsNullOrWhiteSpace(category))
        {
            var cat = config.Categories.FirstOrDefault(c => c.Name.Equals(category, StringComparison.OrdinalIgnoreCase));
            if (cat != null && cat.ProwlarrCategories.Any())
            {
                categoryIds = cat.ProwlarrCategories;
            }
        }

        // Execute searches
        if (config.EnableProwlarr)
        {
            var prowlarrResults = await _prowlarr.SearchAsync(query, categoryIds, cancellationToken);
            results.AddRange(prowlarrResults);
        }

        if (config.EnableTorznab)
        {
            var torznabResults = await _torznab.SearchAsync(query, categoryIds, cancellationToken);
            results.AddRange(torznabResults);
        }

        // Apply Gold/Freeleech filter for regular users if configured
        if (!isUserAdmin && config.OnlyGoldTorrentsForNonAdmins)
        {
            results = results.Where(r => r.IsGold || r.IsFreeleech).ToList();
        }

        // Deduplicate by InfoHash if present, otherwise by Title
        var seenHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = new List<TorrentSearchResult>();
        foreach (var r in results)
        {
            if (!string.IsNullOrWhiteSpace(r.InfoHash))
            {
                if (seenHashes.Add(r.InfoHash))
                {
                    deduped.Add(r);
                }
            }
            else
            {
                deduped.Add(r);
            }
        }

        return deduped
            .OrderByDescending(r => r.IsGold ? 2 : (r.IsFreeleech ? 1 : 0))
            .ThenByDescending(r => r.Seeders)
            .ToList();
    }

    public async Task<Dictionary<string, bool>> TestConnectionsAsync(CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var status = new Dictionary<string, bool>();

        if (config.EnableProwlarr)
        {
            status["Prowlarr"] = await _prowlarr.TestConnectionAsync(cancellationToken);
        }

        if (config.EnableTorznab)
        {
            status["Torznab"] = await _torznab.TestConnectionAsync(cancellationToken);
        }

        return status;
    }
}
