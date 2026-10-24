using Jellyfin.Plugin.Torr2Jelly.Api.Dtos;
using Jellyfin.Plugin.Torr2Jelly.Configuration;
using Jellyfin.Plugin.Torr2Jelly.Search;
using Jellyfin.Plugin.Torr2Jelly.Search.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torr2Jelly.Engine;

public sealed class MovieDiscoveryService(TmdbClient tmdb, ImdbRatings ratings, StrmEngine engine,
    TorrentSearchService search, TorrClientService torr, TorrentLinkResolver links, IHttpClientFactory factory,
    ILibraryManager library, ILogger<MovieDiscoveryService> logger)
{
    private readonly SemaphoreSlim _addLock = new(1, 1);

    public async Task<List<DiscoveryMovieDto>> TrendingAsync(string window, CancellationToken ct)
    {
        var movies = await tmdb.GetTrendingMoviesAsync(window, ct);
        var snapshot = await Task.Run(engine.BuildLibraryIdSnapshot, ct);
        foreach (var movie in movies) movie.InLibraryAs = snapshot.Lookup("movie", movie.TmdbId, null);
        return movies;
    }

    public async Task<DiscoveryMovieDto> DetailsAsync(int id, CancellationToken ct)
    {
        var movie = await tmdb.GetMovieDetailsAsync(id, ct);
        var rating = await ratings.GetAsync(movie.ImdbId, ct);
        movie.ImdbRating = rating?.Score;
        movie.ImdbVotes = rating?.Votes;
        movie.InLibraryAs = (await Task.Run(engine.BuildLibraryIdSnapshot, ct)).Lookup("movie", id, movie.ImdbId);
        movie.ItemId = FindItem(id, movie.ImdbId);
        return movie;
    }

    public async Task<DiscoveryAddResultDto> AddAsync(int id, CancellationToken ct)
    {
        await _addLock.WaitAsync(ct);
        try
        {
            // Repair local journals before reading library state, even when TorrServer is unavailable.
            await RecoverCoreAsync(ct);
            var movie = await tmdb.GetMovieDetailsAsync(id, ct);
            if (engine.BuildLibraryIdSnapshot().Lookup("movie", id, movie.ImdbId) == LibraryIdSnapshot.AsStream)
                return new() { Status = "exists", Reason = "in_library", ItemId = FindItem(id, movie.ImdbId) };
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var categories = config.Categories.Count > 0 ? config.Categories : PluginConfiguration.GetDefaultCategories();
            var movieCategories = categories.Where(c => string.Equals(c.Type, "movie", StringComparison.OrdinalIgnoreCase)).ToList();
            var category = (movie.IsAnimation ? movieCategories.FirstOrDefault(c => c.Name.Equals("Cartoons", StringComparison.OrdinalIgnoreCase) || c.Name.Contains("мульт", StringComparison.OrdinalIgnoreCase)) : null)
                ?? movieCategories.FirstOrDefault(c => c.IsDefault) ?? movieCategories.FirstOrDefault();
            if (category == null) throw new DiscoveryException("category_unavailable");
            // IDs and names come from TMDB, never from the request body. Use the existing Latin folder naming.
            var folderTitle = movie.OriginalTitle ?? movie.Title;
            if (folderTitle.Any(c => c > 127)) folderTitle = await tmdb.GetEnglishTitleAsync("movie", id, ct) ?? folderTitle;
            var knownPlaceholder = engine.FindMoviePlaceholderDirectory(id, movie.ImdbId);
            var placeholder = knownPlaceholder != null
                ? new PlaceholderResult { Status = PlaceholderResult.StatusExists, Reason = PlaceholderResult.ReasonPlaceholderExists, Folder = knownPlaceholder }
                : await engine.CreateMoviePlaceholderAsync(category.Name, folderTitle, movie.Year, movie.ImdbId ?? "", id, ct);
            if (placeholder.Status == PlaceholderResult.StatusError || placeholder.Folder == null)
                throw new DiscoveryException(placeholder.Reason ?? "write_failed");
            if (placeholder.Reason == PlaceholderResult.ReasonFolderHasStream)
                return new() { Status = "exists", Reason = "in_library", ItemId = FindItem(id, movie.ImdbId) };

            var result = new DiscoveryAddResultDto { ItemId = FindItem(id, movie.ImdbId) };
            var releases = new List<TorrentSearchResult>();
            var searchFailed = false;
            var queries = new[] { movie.ImdbId, (movie.OriginalTitle ?? movie.Title) + " " + movie.Year, movie.Title + " " + movie.Year }
                .Where(q => !string.IsNullOrWhiteSpace(q)).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var query in queries)
            {
                try
                {
                    var found = await search.SearchCheckedAsync(query!, category.Name, ct);
                    releases.AddRange(found.Results);
                    searchFailed |= found.HadFailures;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    searchFailed = true;
                    logger.LogWarning("Discovery search failed ({Type})", ex.GetType().Name);
                }
            }
            // No hash dedup before identity checks: one indexer may omit IDs that another supplies.
            var verified = new List<TorrentSearchResult>();
            foreach (var release in releases)
            {
                var rejection = MovieReleaseVerifier.CheckResult(movie, release);
                if (rejection == null) verified.Add(release);
                else Reject(result, rejection);
            }
            var candidates = verified.OrderByDescending(MovieReleaseVerifier.HasUkrainianAudioHint).ThenByDescending(r => r.Seeders)
                .ThenBy(r => r.Title, StringComparer.Ordinal).DistinctBy(r => r.DownloadUrl).Take(8).ToList();
            var metadataFailed = false;
            foreach (var release in candidates)
            {
                ct.ThrowIfCancellationRequested();
                byte[]? bytes;
                try { bytes = await DownloadMetadataAsync(release.DownloadUrl, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception) { metadataFailed = true; Reject(result, "metadata_unavailable"); continue; }
                var metadata = bytes == null ? null : TorrentMetadata.Read(bytes);
                if (metadata == null) { Reject(result, "metadata_unavailable"); continue; }
                if (!string.IsNullOrWhiteSpace(release.InfoHash) && !TorrentHash.Equal(metadata.Hash, release.InfoHash))
                { Reject(result, "hash_mismatch"); continue; }
                var fileError = MovieReleaseVerifier.CheckFiles(movie, metadata, out var main);
                if (fileError != null) { Reject(result, fileError); continue; }
                var commitReason = await CommitAsync(movie, release, bytes!, metadata, main!, placeholder.Folder, category.Name, ct);
                if (commitReason == "stream")
                {
                    result.Status = "stream";
                    result.Reason = "verified_id_and_files";
                    result.ReleaseTitle = release.Title;
                    result.Seeders = release.Seeders;
                    result.Size = main!.Length;
                    result.ItemId = FindItem(id, movie.ImdbId);
                    return result;
                }
                if (commitReason == "in_library") return new() { Status = "exists", Reason = "in_library", ItemId = FindItem(id, movie.ImdbId) };
                Reject(result, commitReason);
                if (commitReason is "torrserver_unavailable" or "rollback_pending" or "write_failed") { result.Reason = commitReason; return result; }
            }
            result.Reason = searchFailed ? "search_failed" : metadataFailed ? "metadata_unavailable" : candidates.Count > 0 ? "no_verified_files" : "no_verified_release";
            return result;
        }
        finally { _addLock.Release(); }
    }

    private async Task<string> CommitAsync(DiscoveryMovieDto movie, TorrentSearchResult release, byte[] bytes, TorrentMetadata metadata,
        TorrTorrentFile main, string folder, string category, CancellationToken ct)
    {
        if (!await engine.WithDiscoveryCommitLockAsync(() => Task.FromResult(engine.RecoverDiscoveryFolder(folder)), ct)) return "recovery_pending";
        if (engine.BuildLibraryIdSnapshot().Lookup("movie", movie.TmdbId, movie.ImdbId) == LibraryIdSnapshot.AsStream) return "in_library";
        var current = await torr.ListTorrentsAsync(ct);
        if (current == null) return "torrserver_unavailable";
        if (engine.BuildStrmIndex().TryGetValue(metadata.Hash, out var referenced) && referenced.Any(File.Exists)) return "torrent_already_present";
        // Existing torrents are never taken over, even when an old rejection marker exists.
        if (current.Any(t => TorrentHash.Equal(t.Hash, metadata.Hash)))
            return "torrent_already_present";
        var attempt = engine.BeginDiscoveryAttempt(metadata.Hash);
        var committed = false;
        try
        {
            // The unique temporary server category is an ownership token, not a user library category.
            var torrent = await torr.UploadDiscoveryAsync(bytes, metadata.Hash, attempt, release.Title, movie.PosterUrl, ct);
            if (torrent == null) return "torrserver_unavailable";
            if (!TorrentHash.Equal(torrent.Hash, metadata.Hash)) return "hash_mismatch";
            for (var poll = 0; torrent.GetEffectiveFiles().Count == 0 && poll < 3; poll++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                torrent = await torr.GetTorrentAsync(metadata.Hash, ct) ?? torrent;
            }
            var actual = torrent.GetEffectiveFiles();
            var selected = actual.Where(f => f.Id > 0 && f.Length == main.Length && SamePath(f.Path, main.Path, metadata.Name)).ToList();
            if (!TorrentHash.Equal(torrent.Hash, metadata.Hash) || selected.Count != 1 || actual.Select(f => f.Id).Distinct().Count() != actual.Count) return "file_metadata_mismatch";
            if (actual.Count != metadata.Files.Count || metadata.Files.Any(f => actual.Count(a => a.Length == f.Length && SamePath(a.Path, f.Path, metadata.Name)) != 1)) return "file_metadata_mismatch";
            // The only stream written is the verified movie, with its actual TorrServer file index.
            torrent.FileStats = selected;
            var added = await engine.WithDiscoveryCommitLockAsync(async () =>
            {
                if (!engine.IsCurrentDiscoveryAttempt(metadata.Hash, attempt.Nonce))
                    return new AlternateStreamResultDto { ErrorMessage = "intake_superseded" };
                if (engine.BuildLibraryIdSnapshot().Lookup("movie", movie.TmdbId, movie.ImdbId) == LibraryIdSnapshot.AsStream)
                    return new AlternateStreamResultDto { ErrorMessage = "in_library" };
                return await engine.CommitDiscoveryStreamAsync(folder, torrent, new AddAlternateStreamRequestDto
                {
                    LocalizedTitle = movie.Title, OriginalTitle = movie.OriginalTitle, Year = movie.Year,
                    TmdbId = movie.TmdbId, ImdbId = movie.ImdbId, TorrentTitle = release.Title,
                    Seeders = release.Seeders, Tracker = release.Indexer
                }, ct);
            }, ct);
            if (added.ErrorMessage == "in_library") return "in_library";
            if (added.ErrorMessage == "intake_superseded") return "intake_superseded";
            if (!added.Success) return "write_failed";
            committed = true;
            // A category update cannot turn a completed stream into a failed job. Recovery
            // retries finalization when the server is offline or the request was cancelled.
            using var finalize = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await torr.FinalizeDiscoveryAsync(metadata.Hash, attempt, category, finalize.Token); }
            catch (OperationCanceledException) { logger.LogWarning("Discovery category finalization deferred for {Hash}", metadata.Hash); }
            return "stream";
        }
        finally
        {
            if (!committed)
            {
                // Independent cleanup token: a disconnect must not leave a failed candidate for scheduled sync.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                try { await CleanupAttemptAsync(metadata.Hash, attempt, cleanup.Token); }
                catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
                {
                    logger.LogWarning("Discovery cleanup deferred for {Hash}; the pending marker remains for recovery", metadata.Hash);
                }
            }
        }
    }

    internal async Task RecoverAsync(CancellationToken ct)
    {
        await _addLock.WaitAsync(ct);
        try
        {
            await RecoverCoreAsync(ct);
        }
        finally { _addLock.Release(); }
    }

    private async Task RecoverCoreAsync(CancellationToken ct)
    {
        // Disk repair does not depend on network availability. Only pending leases can grant removal rights.
        var pending = new List<(string Hash, DiscoveryMarker Marker)>();
        foreach (var hash in engine.GetDiscoveryPendingHashes())
        {
            var mayClean = await engine.WithDiscoveryCommitLockAsync(() =>
            {
                if (engine.HasCommittedDiscoveryStream(hash))
                {
                    engine.DiscardDiscoveryJournal(hash);
                    return Task.FromResult(false);
                }
                return Task.FromResult(engine.RecoverDiscoveryJournal(hash));
            }, ct);
            if (engine.GetDiscoveryMarker(hash) is { State: "pending" } marker)
            {
                if (engine.GetCommittedDiscoveryCategory(hash) is { } category)
                    await torr.FinalizeDiscoveryAsync(hash, marker, category, ct);
                else if (mayClean) pending.Add((hash, marker));
            }
            else if (engine.HasCommittedDiscoveryStream(hash)) engine.ClearDiscoveryMarker(hash);
        }
        foreach (var (hash, marker) in pending.Take(32)) await CleanupAttemptAsync(hash, marker, ct);
    }

    private async Task CleanupAttemptAsync(string hash, DiscoveryMarker attempt, CancellationToken ct)
    {
        await torr.CleanupDiscoveryAsync(hash, attempt,
            () => engine.BuildStrmIndex().TryGetValue(hash, out var paths) && paths.Any(File.Exists), ct);
    }

    internal static bool SamePath(string actual, string expected, string root)
    {
        actual = actual.Replace('\\', '/');
        expected = expected.Replace('\\', '/');
        if (actual == expected) return true;
        return expected.StartsWith(root + "/", StringComparison.Ordinal) && actual == expected[(root.Length + 1)..];
    }

    private async Task<byte[]?> DownloadMetadataAsync(string link, CancellationToken ct)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return null;
        var resolved = await links.ResolveAsync(link, ct);
        if (!resolved.Success || !Uri.TryCreate(resolved.Link, UriKind.Absolute, out uri) || uri.Scheme is not ("https" or "http")) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var client = factory.CreateClient("DiscoveryMetadata");
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > TorrentFile.MaxBytes) return null;
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream();
        var chunk = new byte[65536];
        int read;
        while ((read = await input.ReadAsync(chunk, timeout.Token)) > 0)
        {
            if (buffer.Length + read > TorrentFile.MaxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private Guid? FindItem(int tmdbId, string? imdbId) => library.GetItemList(new InternalItemsQuery
    {
        IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Movie }, Recursive = true
    })?.FirstOrDefault(item => item.GetProviderId(MetadataProvider.Tmdb) == tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
        (imdbId != null && item.GetProviderId(MetadataProvider.Imdb) == imdbId))?.Id;

    private static void Reject(DiscoveryAddResultDto result, string reason)
    {
        if (result.Rejections.Count < 20 && !result.Rejections.Contains(reason)) result.Rejections.Add(reason);
    }
}

public sealed class DiscoveryException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
