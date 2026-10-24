using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Torr2Jelly;
using Jellyfin.Plugin.Torr2Jelly.Api;
using Jellyfin.Plugin.Torr2Jelly.Api.Dtos;
using Jellyfin.Plugin.Torr2Jelly.Configuration;
using Jellyfin.Plugin.Torr2Jelly.Daemon;
using Jellyfin.Plugin.Torr2Jelly.Engine;
using Jellyfin.Plugin.Torr2Jelly.Search;
using Jellyfin.Plugin.Torr2Jelly.Search.Models;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging.Abstractions;

var passed = 0;
void Check(bool test, string name)
{
    if (!test) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS " + name); passed++;
}
DiscoveryMovieDto Movie() => new() { TmdbId = 603, ImdbId = "tt0133093", Title = "Матриця", OriginalTitle = "The Matrix", Year = 1999, Titles = new() { "The Matrix", "Матриця" } };
TorrentSearchResult Release() => new() { Title = "The.Matrix.1999.2160p.Remux.UKR", ImdbId = "tt0133093", TmdbId = 603, IsFreeleech = true, Seeders = 3, Size = 80L * 1024 * 1024 * 1024 };
async Task<(string Folder, string Strm, Dictionary<string, byte[]> Before)> StageInterruptedWrite(Fixture f)
{
    f.MissingIds = true;
    await f.Service.AddAsync(603, default);
    var strm = Directory.GetFiles(f.Root, "*.strm", SearchOption.AllDirectories).Single();
    var folder = Path.GetDirectoryName(strm)!;
    var before = Directory.GetFiles(folder).Where(p => p.EndsWith(".strm") || p.EndsWith(".nfo")).ToDictionary(p => Path.GetFileName(p), File.ReadAllBytes);
    f.SetPendingMarker();
    File.WriteAllText(f.MarkerPath + ".rollback.json", JsonSerializer.Serialize(new { Directory = folder, Files = before }));
    File.WriteAllText(strm, "http://torrserver.test/stream?link=" + f.Metadata.Hash + "&index=17&play");
    File.WriteAllText(Path.Combine(folder, "movie.nfo"), "partial write");
    f.Present = true;
    return (folder, strm, before);
}
Check(MovieReleaseVerifier.CheckResult(Movie(), Release()) == null, "4K Remux and large sizes are allowed");
foreach (var (title, expected) in new[] {
    ("The Matrix 1999 1080p", true), ("Матриця / The Matrix (1999) BluRay", true),
    ("The Matrix 2021 1080p", false), ("The Matrix Reloaded 1999 1080p", false),
    ("The Matrix 2 1999 1080p", false), ("The Matrix 1999 2021 Collection", false),
    ("The Matrix 1080p", false), ("Matrix 1999 1080p", false), ("The Matrix S01E01 1999", false) })
{
    var r = Release(); r.Title = title;
    Check((MovieReleaseVerifier.CheckResult(Movie(), r) == null) == expected, "release identity: " + title);
}
var rel = Release(); rel.ImdbId = null; rel.TmdbId = null;
Check(MovieReleaseVerifier.CheckResult(Movie(), rel) == "id_missing", "title/year alone cannot authorize an add");
rel = Release(); rel.TmdbId = 604;
Check(MovieReleaseVerifier.CheckResult(Movie(), rel) == "id_conflict", "matching IMDb never overrides conflicting TMDB");
rel = Release(); rel.ImdbId = "tt9999999";
Check(MovieReleaseVerifier.CheckResult(Movie(), rel) == "id_conflict", "matching TMDB never overrides conflicting IMDb");
rel = Release(); rel.ImdbId = null;
Check(MovieReleaseVerifier.CheckResult(Movie(), rel) == null, "matching TMDB alone can verify identity");
rel = Release(); rel.IsFreeleech = false;
Check(MovieReleaseVerifier.CheckResult(Movie(), rel) == "not_freeleech", "paid release is excluded");
rel = Release(); rel.Seeders = 0;
Check(MovieReleaseVerifier.CheckResult(Movie(), rel) == "no_seeders", "no seeders is excluded");
rel = Release(); rel.Title = "The Matrix 1999 HDCAM";
Check(MovieReleaseVerifier.CheckResult(Movie(), rel) == null, "user's unlimited quality profile also permits camera releases");
Check(MovieReleaseVerifier.HasUkrainianAudioHint(Release()), "explicit UKR language hint");
rel = Release(); rel.Title = "The Matrix 1999 1080p ENG";
Check(!MovieReleaseVerifier.HasUkrainianAudioHint(rel), "unknown/English audio is not Ukrainian");
rel.Title = "The Matrix 1999 ENG UKR Sub";
Check(!MovieReleaseVerifier.HasUkrainianAudioHint(rel), "Ukrainian subtitles alone are not an audio hint");
Check(MovieReleaseVerifier.TitleMatches(Movie(), "The Matrix 1999 1920x1080 WEB-DL", true), "dimensions are not mistaken for another release year");
var numeric = Movie(); numeric.Title = numeric.OriginalTitle = "2001 A Space Odyssey"; numeric.Titles = new() { numeric.Title }; numeric.Year = 1968;
Check(MovieReleaseVerifier.TitleMatches(numeric, "2001.A.Space.Odyssey.1968.1080p", true), "year-like numbers in a movie title survive verification");
var trailer = Movie(); trailer.Title = trailer.OriginalTitle = "Trailer Park Boys The Movie"; trailer.Titles = new() { trailer.Title }; trailer.Year = 2006;
var trailerMetadata = TorrentMetadata.Read(Fixture.Single("Trailer.Park.Boys.The.Movie.2006.mkv"))!;
Check(MovieReleaseVerifier.CheckFiles(trailer, trailerMetadata, out _) == null, "a movie title containing Trailer is not a sample");
var small = TorrentMetadata.Read(Fixture.Single("The.Matrix.1999.mkv"))!;
small.Files[0].Length = 1024;
Check(MovieReleaseVerifier.CheckFiles(Movie(), small, out _) == null, "unlimited size profile has no minimum-size filter");

foreach (var (name, valid) in new[] { ("The.Matrix.1999.mkv", true), ("The.Matrix.mkv", true), ("The.Matrix.Reloaded.2003.mkv", false), ("The.Matrix.2021.mkv", false), ("The.Matrix.S01E01.mkv", false), ("sample.mkv", false), ("The.Matrix.1999.iso", false) })
{
    var metadata = TorrentMetadata.Read(Fixture.Single(name))!;
    Check(metadata != null && (MovieReleaseVerifier.CheckFiles(Movie(), metadata, out _) == null) == valid, "file identity: " + name);
}
Check(TorrentMetadata.Read(Fixture.Single("../The.Matrix.1999.mkv")) == null, "path traversal rejected");
Check(TorrentMetadata.Read(Encoding.UTF8.GetBytes("not a torrent")) == null, "invalid bencode rejected");
Check(TorrentMetadata.Read(Encoding.UTF8.GetBytes("d4:infod6:lengthi900000000e4:name19:The.Matrix.1999.mkv12:meta versioni2eee")) == null, "v2 metadata fails closed");
var pack = new TorrentMetadata(new string('a', 40), "Pack", new() { new() { Path = "The.Matrix.1999.mkv", Length = 900000000 }, new() { Path = "The.Matrix.Reloaded.2003.mkv", Length = 900000000 } });
Check(MovieReleaseVerifier.CheckFiles(Movie(), pack, out _) == "ambiguous_files", "multiple movies rejected");
var attrs = typeof(MovieDiscoveryController).GetCustomAttributes<AuthorizeAttribute>();
Check(attrs.Any(a => a.Policy == "RequiresElevation"), "all discovery actions require administrator privileges");

using (var f = new Fixture())
{
    var details = await f.Service.DetailsAsync(603, default);
    Check(details.Title == "Матриця" && details.Actors.Count == 1 && details.Runtime == 136, "TMDB details, genres and cast are mapped");
    var trending = await f.Service.TrendingAsync("week", default);
    Check(trending.Count == 1 && trending[0].TmdbId == 603, "trending response excludes adult titles");
    var first = await f.Service.AddAsync(603, default);
    Check(first.Status == "stream" && f.Uploads == 1, "verified metadata becomes a real stream");
    var strm = Directory.GetFiles(f.Root, "*.strm", SearchOption.AllDirectories).Single();
    Check(File.ReadAllText(strm).Contains("index=17&play"), "stream uses actual TorrServer file id, never a guessed index");
    var second = await f.Service.AddAsync(603, default);
    Check(second.Status == "exists" && f.Uploads == 1, "repeated add is idempotent before Jellyfin scan");
    await f.Engine.FullSyncAsync(new[] { f.ActualTorrent() }, false);
    Check(Directory.GetFiles(f.Root, "*.strm", SearchOption.AllDirectories).Length == 1, "scheduled sync preserves discovery movie registration");
    var nfo = Directory.GetFiles(f.Root, "movie.nfo", SearchOption.AllDirectories).Single();
    Check(File.ReadAllText(nfo).Contains("tt0133093") && File.ReadAllText(nfo).Contains("603"), "canonical provider ids survive sync");
}
using (var f = new Fixture { LibraryLookupFails = true })
{
    var details = await f.Service.DetailsAsync(603, default);
    Check(details.Title == "Матриця" && details.Actors.Count == 1 && details.ItemId == null, "unavailable Jellyfin library API cannot break movie details or cast");
    var added = await f.Service.AddAsync(603, default);
    Check(added.Status == "stream", "unavailable optional item lookup cannot fail an otherwise verified add");
}
using (var f = new Fixture())
{
    f.LibraryMovie = new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid() };
    MediaBrowser.Model.Entities.ProviderIdsExtensions.SetProviderId(f.LibraryMovie, MediaBrowser.Model.Entities.MetadataProvider.Tmdb, "603");
    var details = await f.Service.DetailsAsync(603, default);
    Check(details.ItemId == f.LibraryMovie.Id && details.InLibraryAs == "stream", "compatible library lookup preserves existing movie links and library detection");
}
using (var f = new Fixture { MissingIds = true })
{
    var result = await f.Service.AddAsync(603, default);
    Check(result.Status == "placeholder" && result.Rejections.Contains("id_missing") && f.Uploads == 0, "missing IDs leave a placeholder without upload");
}
using (var f = new Fixture { SearchFails = true })
{
    var result = await f.Service.AddAsync(603, default);
    Check(result.Reason == "search_failed" && f.Uploads == 0, "provider failure is not reported as no releases");
}
using (var f = new Fixture { WrongSearchHash = true })
{
    var result = await f.Service.AddAsync(603, default);
    Check(result.Rejections.Contains("hash_mismatch") && f.Uploads == 0, "downloaded info-hash must match the search result before upload");
}
using (var f = new Fixture { WrongActualFile = true })
{
    var result = await f.Service.AddAsync(603, default);
    Check(result.Status == "placeholder" && f.Removals > 0, "TorrServer metadata mismatch is rolled back");
    Check(Directory.GetFiles(f.Root, "*.strm", SearchOption.AllDirectories).All(p => File.ReadAllText(p).Contains(StrmEngine.PlaceholderMarker)), "rollback leaves the placeholder intact");
    await f.Engine.FullSyncAsync(new[] { f.ActualTorrent() }, false);
    Check(Directory.GetFiles(f.Root, "*.strm", SearchOption.AllDirectories).All(p => File.ReadAllText(p).Contains(StrmEngine.PlaceholderMarker)), "a stale pre-rollback list cannot resurrect a rejected torrent");
}
using (var f = new Fixture { WrongActualFile = true })
{
    await f.Service.AddAsync(603, default);
    var removals = f.Removals;
    f.Present = true; f.WrongActualFile = false; f.ActualCategory = "Movies";
    await f.RecoverAsync();
    Check(f.Present && f.Removals == removals, "a rejected marker grants no removal rights over a later foreign torrent");
    var retry = await f.Service.AddAsync(603, default);
    Check(retry.Status == "placeholder" && f.Removals == removals && f.Uploads == 1, "discovery never takes over a pre-existing hash with a rejection marker");
    var manual = await f.Engine.SyncTorrentAsync(f.ActualTorrent(), category: "Movies", customTitle: "The Matrix 1999", tmdbId: 603, imdbId: "tt0133093", forceCategory: true, manualIntake: true);
    Check(manual.Success && !File.Exists(f.MarkerPath), "explicit manual intake clears suppression and succeeds");
    await f.RecoverAsync();
    Check(f.Present && f.Removals == removals, "recovery leaves a manually adopted torrent alone");
}
using (var f = new Fixture { Present = true })
{
    f.SetPendingMarker(); f.ActualCategory = "Movies";
    await f.RecoverAsync();
    Check(f.Present && f.Removals == 0, "pending marker without the matching server nonce cannot authorize removal");
    using var marker = JsonDocument.Parse(File.ReadAllText(f.MarkerPath));
    Check(marker.RootElement.GetProperty("State").GetString() == "pending", "an unrecognized instance stays suppressed until explicit manual adoption");
}
using (var f = new Fixture { Present = true, WrongActualFile = true })
{
    f.SetPendingMarker();
    File.WriteAllText(f.MarkerPath, "legacy or interrupted marker");
    File.SetLastWriteTimeUtc(f.MarkerPath, DateTime.UtcNow.AddDays(-1));
    await f.RecoverAsync();
    await f.Engine.FullSyncFromServerAsync(f.Client, false);
    Check(f.Present && f.Removals == 0 && File.Exists(f.MarkerPath) && Directory.GetFiles(f.Root, "*.strm", SearchOption.AllDirectories).Length == 0, "old malformed markers suppress unverified torrents without granting removal rights");
}
using (var f = new Fixture { Present = true })
{
    f.SetPendingMarker(); f.ActualCategory = "Movies";
    File.WriteAllText(f.MarkerPath, JsonSerializer.Serialize(new { State = "rejected", Nonce = Guid.NewGuid(), AtUtc = DateTime.UtcNow.AddMinutes(-20) }));
    await f.RecoverAsync();
    Check(!File.Exists(f.MarkerPath) && f.Present && f.Removals == 0, "expired rejection tombstones are pruned without touching TorrServer");
}
using (var f = new Fixture { SearchFails = true })
{
    Plugin.Instance!.Configuration.EnableTorznab = true;
    Plugin.Instance.Configuration.TorznabUrl = "http://torznab.test/api";
    Plugin.Instance.Configuration.TorznabApiKey = "test";
    var result = await f.Service.AddAsync(603, default);
    Check(result.Status == "stream", "a failing Prowlarr never discards healthy Torznab results");
}
using (var f = new Fixture { WrongActualFile = true, RemoveFails = true })
{
    await f.Service.AddAsync(603, default);
    await f.Engine.FullSyncAsync(new[] { f.ActualTorrent() }, false);
    Check(Directory.GetFiles(f.Root, "*.strm", SearchOption.AllDirectories).All(p => File.ReadAllText(p).Contains(StrmEngine.PlaceholderMarker)), "failed rollback remains quarantined during scheduled sync");
    Check(Directory.GetFiles(f.Root, f.Metadata.Hash, SearchOption.AllDirectories).Length == 1, "quarantine persists to disk across server restarts");
}
using (var f = new Fixture { IgnoreUploadCategory = true, WrongActualFile = true })
{
    var result = await f.Service.AddAsync(603, default);
    Check(result.Status == "placeholder" && !f.Present && f.Removals == 1, "explicit category assignment owns and removes a rejected upload even if multipart category is ignored");
}
using (var f = new Fixture { IgnoreUploadCategory = true, IgnoreSetCategory = true, WrongActualFile = true })
{
    var result = await f.Service.AddAsync(603, default);
    using var marker = JsonDocument.Parse(File.ReadAllText(f.MarkerPath));
    File.WriteAllText(f.MarkerPath, JsonSerializer.Serialize(new { State = "pending", Nonce = marker.RootElement.GetProperty("Nonce").GetGuid(), AtUtc = DateTime.UtcNow.AddMinutes(-20) }));
    await f.RecoverAsync();
    await f.Engine.FullSyncFromServerAsync(f.Client, false);
    Check(result.Status == "placeholder" && f.Present && f.Removals == 0, "unprovable ownership never permits removal");
    Check(File.Exists(f.MarkerPath) && Directory.GetFiles(f.Root, "*.strm", SearchOption.AllDirectories).All(p => File.ReadAllText(p).Contains(StrmEngine.PlaceholderMarker)), "an unowned rejected candidate remains suppressed beyond the rejection TTL");
}
using (var f = new Fixture { FailFinalCategory = true })
{
    var result = await f.Service.AddAsync(603, default);
    Check(result.Status == "stream" && File.Exists(f.MarkerPath), "category finalization failure preserves a successful stream and its recovery lease");
    f.FailFinalCategory = false;
    await f.RecoverAsync();
    Check(f.ActualCategory == "Movies" && !File.Exists(f.MarkerPath) && f.Removals == 0, "recovery finalizes the committed category without removing the torrent");
}
using (var f = new Fixture { Present = true })
{
    f.SetPendingMarker();
    f.ManualAddStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    f.ReleaseManualAdd = new(TaskCreationOptions.RunContinuationsAsynchronously);
    var manual = f.Client.AddTorrentAsync("http://tracker.test/unknown-hash.torrent");
    await f.ManualAddStarted.Task;
    var recovery = f.RecoverAsync();
    f.ReleaseManualAdd.TrySetResult();
    await Task.WhenAll(manual, recovery);
    Check(f.Present && f.Removals == 0 && !File.Exists(f.MarkerPath), "manual add without a category or sync supersedes cleanup even when its hash is only returned by the server");
}
using (var f = new Fixture { Present = true })
{
    f.SetPendingMarker();
    await f.Client.AddTorrentFileAsync(Fixture.Single("The.Matrix.1999.mkv"));
    await f.RecoverAsync();
    Check(f.Present && f.Removals == 0 && !File.Exists(f.MarkerPath), "manual file upload revokes a pending lease without needing library sync");
}
using (var f = new Fixture { Present = true })
{
    f.SetPendingMarker();
    var ownedCategory = f.ActualCategory;
    Check(!await f.BackfillAsync("Movies") && File.Exists(f.MarkerPath) && f.ActualCategory == ownedCategory, "automatic category backfill cannot revoke an active discovery lease");
    f.ActualCategory = "";
    Check(!await f.BackfillAsync("Movies") && File.Exists(f.MarkerPath), "backfill skips a pending hash even when its server category is missing");
    File.Delete(f.MarkerPath);
    Check(await f.BackfillAsync("Movies") && f.ActualCategory == "Movies", "automatic backfill still fills an uncategorized ordinary torrent");
    f.ActualCategory = "Cartoons";
    Check(!await f.BackfillAsync("Movies") && f.ActualCategory == "Cartoons", "stale backfill cannot overwrite a newer category");
}
using (var f = new Fixture { WrongActualFile = true, QueueManualAfterUpload = true })
{
    try
    {
        var result = await f.Service.AddAsync(603, default);
        Check(result.Status == "placeholder" && result.Rejections.Contains("file_metadata_mismatch"), "cleanup waiting behind a slow manual add preserves the actual rejection result");
        Check(File.Exists(f.MarkerPath), "timed-out cleanup leaves durable suppression for recovery");
    }
    finally { f.ReleaseManualAdd?.TrySetResult(); }
    if (f.SlowManualAdd != null) await f.SlowManualAdd;
    await f.RecoverAsync();
    Check(f.Present && f.Removals == 0, "deferred recovery respects the completed manual adoption");
}
using (var f = new Fixture { Present = true })
{
    var result = await f.Service.AddAsync(603, default);
    Check(result.Status == "placeholder" && f.Uploads == 0 && f.Removals == 0, "existing TorrServer torrent is never mutated or removed");
}
using (var f = new Fixture())
{
    var pair = await Task.WhenAll(f.Service.AddAsync(603, default), f.Service.AddAsync(603, default));
    Check(pair.Count(r => r.Status == "stream") == 1 && f.Uploads == 1, "concurrent adds create one stream");
}
using (var f = new Fixture { MissingIds = true })
{
    await f.Service.AddAsync(603, default);
    var strm = Directory.GetFiles(f.Root, "*.strm", SearchOption.AllDirectories).Single();
    var folder = Path.GetDirectoryName(strm)!;
    var before = Directory.GetFiles(folder).Where(p => p.EndsWith(".strm") || p.EndsWith(".nfo")).ToDictionary(p => Path.GetFileName(p), File.ReadAllBytes);
    var marker = Path.Combine(f.Root, "data", "torr2jelly-discovery-pending", f.Metadata.Hash);
    Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
    f.SetPendingMarker();
    File.WriteAllText(marker + ".rollback.json", JsonSerializer.Serialize(new { Directory = folder, Files = before }));
    File.WriteAllText(strm, "http://torrserver.test/stream?link=" + f.Metadata.Hash + "&index=17&play");
    File.WriteAllText(Path.Combine(folder, "movie.nfo"), "partial write");
    f.Present = true;
    await (Task)typeof(MovieDiscoveryService).GetMethod("RecoverAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(f.Service, new object[] { CancellationToken.None })!;
    Check(File.ReadAllText(strm).Contains(StrmEngine.PlaceholderMarker), "startup recovery restores a placeholder after a simulated mid-commit crash");
    Check(before.All(p => File.ReadAllBytes(Path.Combine(folder, p.Key)).SequenceEqual(p.Value)), "crash recovery restores all original NFO and STRM bytes");
    Check(!f.Present && File.Exists(marker) && !File.Exists(marker + ".rollback.json"), "recovery removes owned torrent, retains rejection tombstone and finishes journal");
    f.MissingIds = false;
    var retry = await f.Service.AddAsync(603, default);
    Check(retry.Status == "stream", "a recovered movie can be retried successfully: " + JsonSerializer.Serialize(retry));
}
using (var f = new Fixture())
{
    var interrupted = await StageInterruptedWrite(f);
    f.ServerUnavailable = true;
    await f.RecoverAsync();
    Check(interrupted.Before.All(p => File.ReadAllBytes(Path.Combine(interrupted.Folder, p.Key)).SequenceEqual(p.Value)), "journal restoration works while TorrServer is offline");
    Check(f.Present && f.Removals == 0, "offline disk recovery does not claim a successful torrent cleanup");
}
using (var f = new Fixture())
{
    var interrupted = await StageInterruptedWrite(f);
    var newer = "http://torrserver.test/stream?link=" + new string('b', 40) + "&index=4&play";
    File.WriteAllText(interrupted.Strm, newer);
    File.WriteAllText(Path.Combine(interrupted.Folder, "movie.nfo"), "newer metadata");
    f.ServerUnavailable = true;
    await f.RecoverAsync();
    Check(File.ReadAllText(interrupted.Strm) == newer, "a stale journal never overwrites a newer stream");
    Check(File.ReadAllText(Path.Combine(interrupted.Folder, "movie.nfo")) == "newer metadata", "a stale journal also preserves the newer movie metadata");
    Check(!File.Exists(f.MarkerPath + ".rollback.json"), "superseded journals are discarded instead of retried forever");
}
using (var f = new Fixture())
{
    var interrupted = await StageInterruptedWrite(f);
    var newer = f.ActualTorrent(); newer.Hash = new string('b', 40);
    var added = await f.Engine.AddAlternateStreamAsync(interrupted.Folder, newer, new AddAlternateStreamRequestDto { LocalizedTitle = "Матриця", OriginalTitle = "The Matrix", TmdbId = 603, ImdbId = "tt0133093", Year = 1999 });
    Check(added.Success && !File.Exists(f.MarkerPath + ".rollback.json"), "a new commit repairs an older folder journal before writing");
    var bytes = File.ReadAllBytes(added.StrmFilePath);
    f.ServerUnavailable = true;
    await f.RecoverAsync();
    Check(File.ReadAllBytes(added.StrmFilePath).SequenceEqual(bytes), "later recovery preserves the new committed stream");
}
using (var f = new Fixture { MissingIds = true })
{
    await f.Service.AddAsync(603, default);
    var folder = Path.GetDirectoryName(Directory.GetFiles(f.Root, "*.strm", SearchOption.AllDirectories).Single())!;
    var original = Directory.GetFiles(folder).Where(p => p.EndsWith(".strm") || p.EndsWith(".nfo")).ToDictionary(p => Path.GetFileName(p), File.ReadAllBytes);
    f.MissingIds = false;
    await f.Service.AddAsync(603, default);
    var committed = Directory.GetFiles(folder).Where(p => p.EndsWith(".strm") || p.EndsWith(".nfo")).ToDictionary(p => Path.GetFileName(p), File.ReadAllBytes);
    f.SetPendingMarker();
    File.WriteAllText(f.MarkerPath + ".rollback.json", JsonSerializer.Serialize(new { Directory = folder, Files = original }));
    await f.Engine.SyncTorrentAsync(f.ActualTorrent(), manualIntake: true);
    Check(committed.All(p => File.ReadAllBytes(Path.Combine(folder, p.Key)).SequenceEqual(p.Value)), "manual same-hash sync discards a deferred journal instead of reverting a committed stream");
    Check(!File.Exists(f.MarkerPath + ".rollback.json"), "the deferred journal is discarded after recognizing the committed stream");
}
using (var f = new Fixture())
{
    await f.Service.AddAsync(603, default);
    var marker = Path.Combine(f.Root, "data", "torr2jelly-discovery-pending", f.Metadata.Hash);
    File.WriteAllText(marker, "crash after successful registration");
    await (Task)typeof(MovieDiscoveryService).GetMethod("RecoverAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(f.Service, new object[] { CancellationToken.None })!;
    Check(!File.Exists(marker) && f.Removals == 0, "startup recovery recognizes a completed stream and never removes it");
    foreach (var file in Directory.GetFiles(f.Root, "*.strm", SearchOption.AllDirectories)) File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-1));
    await f.Engine.FullSyncFromServerAsync(f.Client, true);
    Check(Directory.GetFiles(f.Root, "*.strm", SearchOption.AllDirectories).Length == 1, "explicit purge uses a fresh server list and preserves a verified stream");
}
using (var f = new Fixture())
using (var jobs = new DiscoveryJobs(f.Service, NullLogger<DiscoveryJobs>.Instance))
{
    var first = jobs.Start(603);
    Check(jobs.Start(603).JobId == first.JobId, "background jobs deduplicate in-flight adds");
    await jobs.StartAsync(default);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    while (jobs.Get(first.JobId)?.Status is "queued" or "running") await Task.Delay(30, deadline.Token);
    Check(jobs.Get(first.JobId)?.Result?.Status == "stream", "detached background job completes without an HTTP request token");
    await jobs.StopAsync(default);
}
using (var f = new Fixture())
{
    var file = Path.Combine(f.Root, "data", "torr2jelly", "imdb-ratings.tsv.gz");
    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
    using (var gzip = new GZipStream(File.Create(file), CompressionMode.Compress))
    using (var writer = new StreamWriter(gzip)) { writer.Write("tconst\taverageRating\tnumVotes\ntt0133093\t8.7\t2100000\n"); }
    var rating = await f.Ratings.GetAsync("tt0133093");
    Check(rating?.Score == 8.7 && rating.Votes == 2100000, "IMDb ratings read from compressed disk cache");
    Check(await f.Ratings.GetAsync("tt9999999") == null, "missing IMDb rating remains unavailable");
    Check(await f.Ratings.GetAsync("../malicious") == null, "invalid IMDb id is rejected");
    File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-2));
    var refresh = typeof(ImdbRatings).GetMethod("RefreshAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    try { await (Task)refresh.Invoke(f.Ratings, new object[] { CancellationToken.None })!; }
    catch (InvalidDataException) { }
    Check((await f.Ratings.GetAsync("tt0133093"))?.Score == 8.7, "bad ratings refresh preserves the previous cache");
}
Console.WriteLine($"{passed} checks passed.");

public class Proxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Handler = (_, _) => null;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var result = Handler(method!, args);
        if (result != null) return result;
        var type = method!.ReturnType;
        if (type == typeof(void)) return null;
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}

public sealed class Fixture : HttpMessageHandler, IHttpClientFactory, IDisposable
{
    public string Root = Path.Combine(Path.GetTempPath(), "t2j-discovery-test-" + Guid.NewGuid().ToString("N"));
    public bool SearchFails, MissingIds, WrongActualFile, RemoveFails, Present, WrongSearchHash, ServerUnavailable;
    public bool IgnoreUploadCategory, IgnoreSetCategory, FailFinalCategory;
    public bool QueueManualAfterUpload;
    public bool LibraryLookupFails;
    public BaseItem? LibraryMovie;
    public Task<TorrTorrent?>? SlowManualAdd;
    public TaskCompletionSource? ManualAddStarted, ReleaseManualAdd;
    public string ActualCategory = "Movies";
    public int Uploads, Removals;
    public TorrentMetadata Metadata;
    public StrmEngine Engine;
    public ImdbRatings Ratings;
    public MovieDiscoveryService Service;
    public TorrClientService Client;
    private byte[] _torrent = Single("The.Matrix.1999.mkv");
    public HttpClient CreateClient(string name) => new(this, false);
    public static byte[] Single(string name) => Encoding.UTF8.GetBytes("d4:infod6:lengthi900000000e4:name" + Encoding.UTF8.GetByteCount(name) + ":" + name + "ee");

    public Fixture()
    {
        Metadata = TorrentMetadata.Read(_torrent)!;
        Directory.CreateDirectory(Root);
        var paths = DispatchProxy.Create<IApplicationPaths, Proxy>();
        ((Proxy)(object)paths).Handler = (m, _) => m.Name.StartsWith("get_") ? m.Name switch {
            "get_DataPath" => Path.Combine(Root, "data"), "get_ConfigurationDirectoryPath" or "get_PluginConfigurationsPath" => Path.Combine(Root, "config"),
            _ => Root } : null;
        Directory.CreateDirectory(Path.Combine(Root, "data")); Directory.CreateDirectory(Path.Combine(Root, "config"));
        var plugin = new Plugin(paths, DispatchProxy.Create<IXmlSerializer, Proxy>());
        if (plugin.Configuration == null) typeof(MediaBrowser.Common.Plugins.BasePlugin<PluginConfiguration>).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .First(f => f.FieldType == typeof(PluginConfiguration)).SetValue(plugin, new PluginConfiguration());
        var cfg = plugin.Configuration!;
        cfg.MediaRootPath = Path.Combine(Root, "media"); Directory.CreateDirectory(cfg.MediaRootPath);
        cfg.Categories = new() { new("Movies", "movies", "movie", new() { 2000 }, true) };
        cfg.TmdbApiKey = "test-key"; cfg.EnableProwlarr = true; cfg.EnableTorznab = false;
        cfg.ProwlarrUrl = "http://indexer.test"; cfg.ProwlarrApiKey = "private-test-key";
        cfg.ExternalTorrServerUrl = "http://torrserver.test"; cfg.TorrServerStreamUrl = "http://torrserver.test";
        var lib = DispatchProxy.Create<ILibraryManager, Proxy>();
        ((Proxy)(object)lib).Handler = (m, _) => m.Name switch { "GetItemList" => LibraryLookupFails ? throw new MissingMethodException("GetItemList ABI changed") : LibraryMovie == null ? new List<BaseItem>() : new List<BaseItem> { LibraryMovie }, "GetVirtualFolders" => new List<MediaBrowser.Model.Entities.VirtualFolderInfo>(), _ => null };
        var daemon = new TorrServerDaemonManager(NullLogger<TorrServerDaemonManager>.Instance, this, paths);
        Engine = new StrmEngine(NullLogger<StrmEngine>.Instance, lib, DispatchProxy.Create<ILibraryMonitor, Proxy>(), paths, daemon, this);
        Ratings = new ImdbRatings(paths, this, NullLogger<ImdbRatings>.Instance);
        var tmdb = new TmdbClient(NullLogger<TmdbClient>.Instance, this);
        var search = new TorrentSearchService(NullLogger<TorrentSearchService>.Instance, new ProwlarrSearchProvider(NullLogger<ProwlarrSearchProvider>.Instance, this), new TorznabSearchProvider(NullLogger<TorznabSearchProvider>.Instance, this));
        Client = new TorrClientService(NullLogger<TorrClientService>.Instance, this, daemon, paths);
        Service = new MovieDiscoveryService(tmdb, Ratings, Engine, search, Client, new TorrentLinkResolver(NullLogger<TorrentLinkResolver>.Instance, this), this, lib, NullLogger<MovieDiscoveryService>.Instance);
    }
    public string MarkerPath => Path.Combine(Root, "data", "torr2jelly-discovery-pending", Metadata.Hash);
    public Guid SetPendingMarker()
    {
        var nonce = Guid.NewGuid();
        Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
        File.WriteAllText(MarkerPath, JsonSerializer.Serialize(new { State = "pending", Nonce = nonce, AtUtc = DateTime.UtcNow }));
        ActualCategory = "t2j-pending-" + nonce.ToString("N");
        return nonce;
    }
    public Task RecoverAsync() => (Task)typeof(MovieDiscoveryService).GetMethod("RecoverAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Service, new object[] { CancellationToken.None })!;
    public Task<bool> BackfillAsync(string category) => (Task<bool>)typeof(TorrClientService).GetMethod("BackfillTorrentCategoryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Client, new object[] { Metadata.Hash, category, CancellationToken.None })!;
    public TorrTorrent ActualTorrent() => new() { Hash = Metadata.Hash, Title = "The Matrix 1999", Category = ActualCategory, FileStats = new() { new() { Id = 17, Path = WrongActualFile ? "Alien.1979.mkv" : "The.Matrix.1999.mkv", Length = 900000000 } } };
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        if (uri.Host == "api.themoviedb.org")
        {
            var movie = new { id = 603, title = "Матриця", original_title = "The Matrix", imdb_id = "tt0133093", release_date = "1999-03-30", overview = "Опис", adult = false, runtime = 136, vote_average = 8.2, vote_count = 100, genres = new[] { new { id = 28, name = "Бойовик" } }, credits = new { cast = new[] { new { name = "Keanu Reeves", character = "Neo", profile_path = "/actor.jpg" } } }, alternative_titles = new { titles = new[] { new { title = "Матриця" } } } };
            return Json(uri.AbsolutePath.Contains("trending") ? new { results = new object[] { movie, new { id = 999, adult = true } } } : movie);
        }
        if (uri.Host == "indexer.test")
        {
            if (uri.AbsolutePath.StartsWith("/download")) return new(HttpStatusCode.OK) { Content = new ByteArrayContent(_torrent) };
            if (SearchFails) return new(HttpStatusCode.ServiceUnavailable);
            return Json(new[] { new { title = "The Matrix 1999 2160p Remux UKR", size = 900000000, seeders = 7, protocol = "torrent", indexer = "Test", infoHash = WrongSearchHash ? new string('b', 40) : Metadata.Hash, downloadUrl = "http://indexer.test/download/fixture", imdbId = MissingIds ? (long?)null : 133093, tmdbId = MissingIds ? (int?)null : 603, indexerFlags = new[] { "freeleech" } } });
        }
        if (uri.Host == "torrserver.test")
        {
            if (ServerUnavailable) return new(HttpStatusCode.ServiceUnavailable);
            if (uri.AbsolutePath == "/torrent/upload")
            {
                Uploads++; Present = true;
                var category = ((MultipartFormDataContent)request.Content!).FirstOrDefault(p => p.Headers.ContentDisposition?.Name?.Trim('"') == "category");
                ActualCategory = category == null || IgnoreUploadCategory ? "" : await category.ReadAsStringAsync(ct);
                return Json(ActualTorrent());
            }
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var action = doc.RootElement.GetProperty("action").GetString();
            if (action == "list")
            {
                if (QueueManualAfterUpload && Uploads > 0 && SlowManualAdd == null)
                {
                    ReleaseManualAdd = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    SlowManualAdd = Client.AddTorrentAsync("http://tracker.test/unknown.torrent");
                }
                return Json(Present ? new[] { ActualTorrent() } : Array.Empty<TorrTorrent>());
            }
            if (action == "get") return Json(ActualTorrent());
            if (action == "rem") { Removals++; if (RemoveFails) return new(HttpStatusCode.ServiceUnavailable); Present = false; return Json(new { }); }
            if (action == "add")
            {
                ManualAddStarted?.TrySetResult();
                if (ReleaseManualAdd != null) await ReleaseManualAdd.Task.WaitAsync(ct);
                Present = true;
                // An existing torrent can retain its previous category on a duplicate add.
                return Json(ActualTorrent());
            }
            if (action == "set")
            {
                var next = doc.RootElement.GetProperty("category").GetString() ?? "";
                if (FailFinalCategory && next == "Movies") return new(HttpStatusCode.ServiceUnavailable);
                if (!IgnoreSetCategory) ActualCategory = next;
            }
            return Json(new { });
        }
        if (uri.Host == "torznab.test") return new(HttpStatusCode.OK)
        {
            Content = new StringContent("<rss xmlns:torznab='http://torznab.com/schemas/2015/feed'><channel><item><title>The Matrix 1999 2160p UKR</title><enclosure url='http://indexer.test/download/fixture' length='900000000' type='application/x-bittorrent'/><torznab:attr name='imdb' value='tt0133093'/><torznab:attr name='tmdbid' value='603'/><torznab:attr name='seeders' value='7'/><torznab:attr name='downloadvolumefactor' value='0'/></item></channel></rss>", Encoding.UTF8, "application/xml")
        };
        if (uri.Host == "datasets.imdbws.com") return new(HttpStatusCode.OK) { Content = new StringContent("corrupt gzip fixture") };
        throw new Exception("Unexpected network request: " + uri.Host);
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    protected override void Dispose(bool disposing) { Ratings.Dispose(); base.Dispose(disposing); }
}
