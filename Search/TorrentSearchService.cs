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
        bool? onlyGold = null, // obsolete: accepted for compatibility, ignored (see PluginConfiguration.FreeleechOnly)
        bool isUserAdmin = true,
        CancellationToken cancellationToken = default)
    {
        // A bare IMDb id ("tt0133093") names one title, but says nothing about its kind: ask for both.
        var imdbId = MediaSearchIds.NormalizeImdb(query);
        if (imdbId != null)
        {
            var movie = RunAsync(ProviderQuery.ById(new MediaSearchIds(false, imdbId, null)), category, cancellationToken, false);
            var series = RunAsync(ProviderQuery.ById(new MediaSearchIds(true, imdbId, null)), category, cancellationToken, false);
            await Task.WhenAll(movie, series);
            return MergeDistinct(movie.Result, series.Result).OrderByDescending(r => r.Seeders).ToList();
        }

        return await RunAsync(ProviderQuery.ByText(query), category, cancellationToken, false);
    }

    public async Task<CheckedSearchResult> SearchCheckedAsync(string query, string category, CancellationToken ct)
    {
        var failed = false;
        var results = await RunAsync(ProviderQuery.ByText(query), category, ct, true, () => failed = true);
        return new CheckedSearchResult(results, failed);
    }

    /// <summary>The id search of <see cref="SearchCheckedAsync"/>: failures are reported instead of looking like "no releases".</summary>
    public async Task<CheckedSearchResult> SearchCheckedByIdAsync(MediaSearchIds ids, string category, CancellationToken ct)
    {
        if (!ids.HasAny)
        {
            return new CheckedSearchResult(new List<TorrentSearchResult>(), false);
        }

        var failed = false;
        var results = await RunAsync(ProviderQuery.ById(ids), category, ct, true, () => failed = true);
        return new CheckedSearchResult(results, failed);
    }

    /// <summary>
    /// The releases of one library item (a movie's other versions, a series' seasons). Two kinds of search run side
    /// by side: by id, for the indexers that support it (exact, and independent of the language of the release
    /// names), and by name, for the others. A movie is searched by name and year. A series is searched by its name
    /// alone as well, because later seasons carry the year they aired in, not the year the series began. A title in
    /// a script the trackers do not use (e.g. Japanese) is not searched; the item's next title is only tried when
    /// the one before it found nothing.
    /// </summary>
    public async Task<List<TorrentSearchResult>> SearchForItemAsync(
        MediaSearchIds ids,
        IReadOnlyList<string?> titles,
        int? year,
        bool isUserAdmin = true,
        CancellationToken cancellationToken = default)
    {
        Task<List<TorrentSearchResult>> ByName(string title)
        {
            var queries = new List<string> { SearchTitles.WithYear(title, year) };
            if (ids.IsSeries && year is > 0)
            {
                queries.Insert(0, title);
            }

            return RunManyAsync(queries.Select(ProviderQuery.ByText), cancellationToken);
        }

        var names = SearchTitles.Searchable(titles);
        var byId = ids.HasAny
            ? RunAsync(ProviderQuery.ById(ids), null, cancellationToken, false)
            : Task.FromResult(new List<TorrentSearchResult>());
        var byName = names.Count > 0 ? ByName(names[0]) : Task.FromResult(new List<TorrentSearchResult>());
        await Task.WhenAll(byId, byName);

        var named = byName.Result;
        for (var i = 1; i < names.Count && named.Count == 0; i++)
        {
            named = await ByName(names[i]);
        }

        return RankForItem(MergeDistinct(byId.Result, named), ids, titles, year);
    }

    private async Task<List<TorrentSearchResult>> RunManyAsync(IEnumerable<ProviderQuery> queries, CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(queries.Select(query => RunAsync(query, null, cancellationToken, false)));
        return MergeDistinct(results);
    }

    /// <summary>
    /// Orders the releases found for one item by how sure it is that they are that title, then by seeders:
    /// first the releases with a matching id, or with the item's title as a whole part of their name, or with the
    /// title and the item's year (a movie needs the year: a remake shares the title); then the ones that only contain
    /// the title; then the ones that do not; last the ones whose id names another title. Nothing is removed: a
    /// tracker can file a release under a wrong id, and a name can be written in a way this does not recognise.
    /// </summary>
    internal static List<TorrentSearchResult> RankForItem(
        IEnumerable<TorrentSearchResult> results,
        MediaSearchIds ids,
        IReadOnlyList<string?> titles,
        int? year)
    {
        var phrases = titles.Select(title => SearchTitles.Tokenize(title)).Where(words => words.Length > 0).ToList();

        int Relevance(TorrentSearchResult result)
        {
            switch (ids.Matches(result.ImdbId, result.TmdbId, result.TvdbId))
            {
                case true:
                    return 3;
                case false:
                    return 0;
            }

            var match = SearchTitles.Match(result.Title, phrases);
            if (match == TitleMatch.None)
            {
                return 1;
            }

            var hasYear = SearchTitles.ContainsYear(result.Title, year);
            var certain = ids.IsSeries
                ? match == TitleMatch.Exact || hasYear
                : hasYear || (match == TitleMatch.Exact && year is not > 0);
            return certain ? 3 : 2;
        }

        return results
            .Select(r => (Result: r, Relevance: Relevance(r)))
            .OrderByDescending(x => x.Relevance)
            .ThenByDescending(x => x.Result.Seeders)
            .Select(x => x.Result)
            .ToList();
    }

    /// <summary>One search as the providers see it: by name, or by id.</summary>
    private readonly record struct ProviderQuery(string? Text, MediaSearchIds? Ids)
    {
        public static ProviderQuery ByText(string text) => new(text, null);

        public static ProviderQuery ById(MediaSearchIds ids) => new(null, ids);
    }

    private Task<List<TorrentSearchResult>> QueryProviderAsync(
        ITorrentSearchProvider provider,
        ProviderQuery query,
        IEnumerable<int>? categoryIds,
        bool reportErrors,
        CancellationToken cancellationToken)
    {
        if (!reportErrors)
        {
            return SearchProviderAsync(provider, query, categoryIds, cancellationToken);
        }

        // The checked variants throw instead of answering with an empty list.
        return (provider, query.Ids) switch
        {
            (ProwlarrSearchProvider prowlarr, null) => prowlarr.SearchCheckedAsync(query.Text!, categoryIds, cancellationToken),
            (ProwlarrSearchProvider prowlarr, var ids) => prowlarr.SearchByIdCheckedAsync(ids, categoryIds, cancellationToken),
            (TorznabSearchProvider torznab, null) => torznab.SearchCheckedAsync(query.Text!, categoryIds, cancellationToken),
            (TorznabSearchProvider torznab, var ids) => torznab.SearchByIdCheckedAsync(ids, categoryIds, cancellationToken),
            _ => SearchProviderAsync(provider, query, categoryIds, cancellationToken)
        };
    }

    private async Task<List<TorrentSearchResult>> RunAsync(ProviderQuery query, string? category, CancellationToken cancellationToken, bool reportErrors, Action? onFailure = null)
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
            searches.Add(QueryProviderAsync(_prowlarr, query, categoryIds, reportErrors, cancellationToken));
        }

        if (config.EnableTorznab)
        {
            searches.Add(QueryProviderAsync(_torznab, query, categoryIds, reportErrors, cancellationToken));
        }

        if (reportErrors && searches.Count == 0) throw new InvalidOperationException("search_unavailable");

        async Task<List<TorrentSearchResult>> Observe(Task<List<TorrentSearchResult>> task)
        {
            try { return await task; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { onFailure?.Invoke(); return new List<TorrentSearchResult>(); }
        }
        var perProvider = await Task.WhenAll(searches.Select(task => reportErrors ? Observe(task) : task));
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

        // One switch for everyone (admins and users alike): only releases that do not count against the download
        // statistics. The old onlyGold / tag-rule options no longer influence the result. Filtering happens before the
        // de-duplication, so a non-free copy of a release can never shadow its free copy.
        if (config.FreeleechOnly)
        {
            results = results.Where(r => r.IsFreeleech).ToList();
        }

        var deduped = reportErrors ? results : DeduplicateByHash(results);

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
        ProviderQuery query,
        IEnumerable<int>? categoryIds,
        CancellationToken cancellationToken)
    {
        try
        {
            var results = query.Ids != null
                ? await provider.SearchByIdAsync(query.Ids, categoryIds, cancellationToken)
                : await provider.SearchAsync(query.Text ?? string.Empty, categoryIds, cancellationToken);
            return results ?? new List<TorrentSearchResult>();
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

    /// <summary>
    /// Joins the results of several searches for the same title, in the given order, without repeating a release
    /// that an earlier list already holds (same info-hash, same download link, or same indexer entry).
    /// </summary>
    internal static List<TorrentSearchResult> MergeDistinct(params IEnumerable<TorrentSearchResult>[] lists)
    {
        // Ordinal: download links carry case-sensitive tokens.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<TorrentSearchResult>();

        foreach (var result in lists.SelectMany(list => list))
        {
            var keys = new List<string>(3);
            var hash = InfoHashHelper.Normalize(result.InfoHash);
            if (hash.Length > 0)
            {
                keys.Add("hash:" + hash);
            }

            if (!string.IsNullOrWhiteSpace(result.DownloadUrl))
            {
                keys.Add("link:" + result.DownloadUrl.Trim());
            }

            if (!string.IsNullOrWhiteSpace(result.Id))
            {
                keys.Add("id:" + result.Indexer + "\u001f" + result.Id.Trim());
            }

            if (keys.Any(seen.Contains))
            {
                continue;
            }

            foreach (var key in keys)
            {
                seen.Add(key);
            }

            merged.Add(result);
        }

        return merged;
    }

    private static bool IsBetterCopy(TorrentSearchResult candidate, TorrentSearchResult current)
    {
        if (candidate.IsFreeleech != current.IsFreeleech)
        {
            return candidate.IsFreeleech;
        }

        return candidate.Seeders > current.Seeders;
    }

    /// <summary>
    /// A snapshot of DisabledTrackers + the enabled flag of TrackerRules. Every lookup is built with
    /// GroupBy(...).First(), so duplicate or null rules in a hand-edited config cannot throw. The per-rule tag
    /// allow-lists of older versions are deliberately not evaluated any more.
    /// </summary>
    private sealed class TrackerPolicy
    {
        private readonly HashSet<string> _disabled;
        private readonly Dictionary<string, bool> _enabled;

        private TrackerPolicy(HashSet<string> disabled, Dictionary<string, bool> enabled)
        {
            _disabled = disabled;
            _enabled = enabled;
        }

        public bool HasAnyRestriction => _disabled.Count > 0 || _enabled.Count > 0;

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

            var enabled = (config.TrackerRules ?? new List<TrackerRule>())
                .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Tracker))
                .GroupBy(r => r.Tracker.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.First().Enabled,
                    StringComparer.OrdinalIgnoreCase);

            return new TrackerPolicy(disabled, enabled);
        }

        /// <summary>Same test <see cref="TorrentSearchService.GetAvailableIndexersAsync"/> reports as "disabled in plugin".</summary>
        public bool IsDisabled(string? indexer)
        {
            var name = indexer?.Trim() ?? string.Empty;
            if (_disabled.Contains(name))
            {
                return true;
            }

            return _enabled.TryGetValue(name, out var isEnabled) && !isEnabled;
        }

        public bool Allows(TorrentSearchResult result) => !IsDisabled(result.Indexer);
    }
}
