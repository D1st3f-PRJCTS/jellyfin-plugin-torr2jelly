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
        var policy = TrackerPolicy.FromConfig(config);

        var list = new List<IndexerStatusDto>();
        if (config.EnableProwlarr)
        {
            var prowlarrIndexers = await _prowlarr.GetIndexersAsync(cancellationToken);
            foreach (var idx in prowlarrIndexers)
            {
                list.Add(new IndexerStatusDto
                {
                    Id = idx.Id,
                    Name = idx.Name,
                    EnabledInProwlarr = idx.Enable,
                    DisabledInPlugin = policy.IsDisabled(idx.Name)
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

        // Determine category IDs
        List<int>? categoryIds = null;
        if (!string.IsNullOrWhiteSpace(category) && config.Categories != null)
        {
            var cat = config.Categories.FirstOrDefault(c =>
                c != null && c.Name != null && c.Name.Equals(category, StringComparison.OrdinalIgnoreCase));
            if (cat != null && cat.ProwlarrCategories != null && cat.ProwlarrCategories.Any())
            {
                categoryIds = cat.ProwlarrCategories;
            }
        }

        // Providers run in parallel; a failing provider only loses its own results.
        var searches = new List<Task<List<TorrentSearchResult>>>();
        if (config.EnableProwlarr)
        {
            searches.Add(SearchProviderAsync(_prowlarr, query, categoryIds, cancellationToken));
        }

        if (config.EnableTorznab)
        {
            searches.Add(SearchProviderAsync(_torznab, query, categoryIds, cancellationToken));
        }

        var perProvider = await Task.WhenAll(searches);
        var results = perProvider
            .SelectMany(r => r)
            .Where(r => r != null)
            .ToList();

        // Filter by TrackerRules (enabled status + allowed tags per tracker) and DisabledTrackers.
        // The same policy backs GetAvailableIndexersAsync, so what the UI shows as disabled is what is filtered here.
        var policy = TrackerPolicy.FromConfig(config);
        if (policy.HasAnyRestriction)
        {
            results = results.Where(r => policy.Allows(r)).ToList();
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

        var deduped = DeduplicateByHash(results);

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

    /// <summary>
    /// Runs one provider. Only the caller cancelling the request propagates (as <see cref="OperationCanceledException"/>,
    /// which is not an error); any other failure, including a provider timeout, yields an empty list.
    /// </summary>
    private async Task<List<TorrentSearchResult>> SearchProviderAsync(
        ITorrentSearchProvider provider,
        string query,
        IEnumerable<int>? categoryIds,
        CancellationToken cancellationToken)
    {
        try
        {
            return await provider.SearchAsync(query, categoryIds, cancellationToken) ?? new List<TorrentSearchResult>();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Search provider {Provider} timed out", provider.Name);
            return new List<TorrentSearchResult>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search provider {Provider} failed", provider.Name);
            return new List<TorrentSearchResult>();
        }
    }

    /// <summary>
    /// Collapses results that share an info-hash (e.g. the same release via Prowlarr and Torznab, or via two
    /// indexers), keeping the best copy: freeleech/gold first, then more seeders. Results without a hash are kept as-is.
    /// </summary>
    private static List<TorrentSearchResult> DeduplicateByHash(List<TorrentSearchResult> results)
    {
        var best = new Dictionary<string, TorrentSearchResult>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var deduped = new List<TorrentSearchResult>();

        foreach (var r in results)
        {
            var hash = InfoHashHelper.Normalize(r.InfoHash);
            if (hash.Length == 0)
            {
                deduped.Add(r);
                continue;
            }

            if (!best.TryGetValue(hash, out var current))
            {
                best[hash] = r;
                order.Add(hash);
            }
            else if (IsBetterCopy(r, current))
            {
                best[hash] = r;
            }
        }

        foreach (var hash in order)
        {
            deduped.Add(best[hash]);
        }

        return deduped;
    }

    private static bool IsBetterCopy(TorrentSearchResult candidate, TorrentSearchResult current)
    {
        if (candidate.IsGold != current.IsGold)
        {
            return candidate.IsGold;
        }

        return candidate.Seeders > current.Seeders;
    }

    /// <summary>
    /// A snapshot of DisabledTrackers + TrackerRules. Every lookup is built with GroupBy(...).First(), so duplicate
    /// or null rules in a hand-edited config cannot throw.
    /// </summary>
    private sealed class TrackerPolicy
    {
        private sealed class RuleEntry
        {
            public bool Enabled { get; init; }

            /// <summary>Canonical allowed tags; null means "no restriction".</summary>
            public HashSet<string>? Allowed { get; init; }
        }

        private readonly HashSet<string> _disabled;
        private readonly Dictionary<string, RuleEntry> _rules;

        private TrackerPolicy(HashSet<string> disabled, Dictionary<string, RuleEntry> rules)
        {
            _disabled = disabled;
            _rules = rules;
        }

        public bool HasAnyRestriction => _disabled.Count > 0 || _rules.Count > 0;

        public static TrackerPolicy FromConfig(PluginConfiguration config)
        {
            var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (config.DisabledTrackers != null)
            {
                foreach (var name in config.DisabledTrackers)
                {
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        disabled.Add(name.Trim());
                    }
                }
            }

            var rules = (config.TrackerRules ?? new List<TrackerRule>())
                .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Tracker))
                .GroupBy(r => r.Tracker.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => ToEntry(g.First()),
                    StringComparer.OrdinalIgnoreCase);

            return new TrackerPolicy(disabled, rules);
        }

        private static RuleEntry ToEntry(TrackerRule rule)
        {
            HashSet<string>? allowed = null;
            if (rule.AllowedTags != null && rule.AllowedTags.Count > 0)
            {
                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (var tag in rule.AllowedTags)
                {
                    if (TorrentTags.TryCanonicalize(tag, out var canonical))
                    {
                        set.Add(canonical);
                    }
                }

                // A rule that only lists tags this plugin does not know would otherwise hide everything
                // from that tracker; treat it as "no restriction" instead.
                allowed = set.Count > 0 ? set : null;
            }

            return new RuleEntry { Enabled = rule.Enabled, Allowed = allowed };
        }

        /// <summary>Same test <see cref="TorrentSearchService.GetAvailableIndexersAsync"/> reports as "disabled in plugin".</summary>
        public bool IsDisabled(string? indexer)
        {
            var name = indexer?.Trim() ?? string.Empty;
            if (_disabled.Contains(name))
            {
                return true;
            }

            return _rules.TryGetValue(name, out var rule) && !rule.Enabled;
        }

        public bool Allows(TorrentSearchResult result)
        {
            var indexer = result.Indexer?.Trim() ?? string.Empty;
            if (IsDisabled(indexer))
            {
                return false;
            }

            if (!_rules.TryGetValue(indexer, out var rule) || rule.Allowed == null)
            {
                return true;
            }

            // "regular" means the release carries no known discount/special tag.
            var hasKnownTag = false;
            if (result.Tags != null)
            {
                foreach (var tag in result.Tags)
                {
                    if (TorrentTags.TryCanonicalize(tag, out var canonical) && canonical != TorrentTags.Regular)
                    {
                        hasKnownTag = true;
                        if (rule.Allowed.Contains(canonical))
                        {
                            return true;
                        }
                    }
                }
            }

            return !hasKnownTag && rule.Allowed.Contains(TorrentTags.Regular);
        }
    }
}
