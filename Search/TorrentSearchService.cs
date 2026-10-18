using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Torr2Jelly.Configuration;
using Jellyfin.Plugin.Torr2Jelly.Search.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torr2Jelly.Search;

public class IndexerStatusDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool EnabledInProwlarr { get; set; }
    public bool DisabledInPlugin { get; set; }
}

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

    public async Task<List<IndexerStatusDto>> GetAvailableIndexersAsync(CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var disabled = new HashSet<string>(config.DisabledTrackers ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

        var list = new List<IndexerStatusDto>();
        if (config.EnableProwlarr)
        {
            var prowlarrIndexers = await _prowlarr.GetIndexersAsync(cancellationToken);
            foreach (var idx in prowlarrIndexers)
            {
                var isExplicitlyDisabled = disabled.Contains(idx.Name);
                if (!isExplicitlyDisabled && config.TrackerRules != null)
                {
                    var rule = config.TrackerRules.FirstOrDefault(r => r.Tracker.Equals(idx.Name, StringComparison.OrdinalIgnoreCase));
                    if (rule != null && !rule.Enabled)
                    {
                        isExplicitlyDisabled = true;
                    }
                }

                list.Add(new IndexerStatusDto
                {
                    Id = idx.Id,
                    Name = idx.Name,
                    EnabledInProwlarr = idx.Enable,
                    DisabledInPlugin = isExplicitlyDisabled
                });
            }
        }

        return list;
    }

    public async Task<List<TorrentSearchResult>> SearchAsync(
        string query,
        string? category = null,
        bool? onlyGold = null,
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

        // Filter by TrackerRules (enabled status + allowed tags per tracker)
        if (config.TrackerRules != null && config.TrackerRules.Count > 0)
        {
            var ruleMap = config.TrackerRules
                .Where(r => !string.IsNullOrWhiteSpace(r.Tracker))
                .ToDictionary(r => r.Tracker.Trim(), r => r, StringComparer.OrdinalIgnoreCase);

            results = results.Where(r =>
            {
                var indexer = r.Indexer?.Trim() ?? string.Empty;
                if (ruleMap.TryGetValue(indexer, out var rule))
                {
                    if (!rule.Enabled)
                    {
                        return false;
                    }

                    if (rule.AllowedTags != null && rule.AllowedTags.Count > 0)
                    {
                        var releaseTags = r.Tags ?? new List<string>();
                        var hasTags = releaseTags.Count > 0;

                        if (!hasTags)
                        {
                            return rule.AllowedTags.Any(t => t.Equals("regular", StringComparison.OrdinalIgnoreCase) ||
                                                             t.Equals("untagged", StringComparison.OrdinalIgnoreCase));
                        }

                        return releaseTags.Any(t => rule.AllowedTags.Any(at => at.Equals(t, StringComparison.OrdinalIgnoreCase)));
                    }

                    return true;
                }

                if (config.DisabledTrackers != null && config.DisabledTrackers.Contains(indexer, StringComparer.OrdinalIgnoreCase))
                {
                    return false;
                }

                return true;
            }).ToList();
        }
        else if (config.DisabledTrackers != null && config.DisabledTrackers.Count > 0)
        {
            var disabled = new HashSet<string>(config.DisabledTrackers, StringComparer.OrdinalIgnoreCase);
            results = results.Where(r => !disabled.Contains(r.Indexer)).ToList();
        }

        // Filter by Gold/Tags according to user parameter or plugin configuration
        var shouldFilterGold = onlyGold ?? config.SearchOnlyGoldByDefault;
        if (!isUserAdmin && config.OnlyGoldTorrentsForNonAdmins)
        {
            shouldFilterGold = true;
        }

        if (shouldFilterGold)
        {
            results = results.Where(r => r.IsGold || (r.Tags != null && r.Tags.Count > 0)).ToList();
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

        if (shouldFilterGold)
        {
            return deduped
                .OrderByDescending(r => (r.Tags != null && r.Tags.Count > 0) ? (r.IsGold ? 3 : 2) : 0)
                .ThenByDescending(r => r.Seeders)
                .ToList();
        }

        return deduped
            .OrderByDescending(r => r.Seeders)
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
