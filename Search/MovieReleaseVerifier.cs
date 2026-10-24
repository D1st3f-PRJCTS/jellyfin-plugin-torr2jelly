using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Torr2Jelly.Api.Dtos;
using Jellyfin.Plugin.Torr2Jelly.Engine;
using Jellyfin.Plugin.Torr2Jelly.Search.Models;

namespace Jellyfin.Plugin.Torr2Jelly.Search;

/// <summary>Identity is a gate, never a score that more seeders or better quality can outweigh.</summary>
public static class MovieReleaseVerifier
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly Regex Episodes = Rx(@"(?:^|[^\p{L}\d])(?:s\d{1,2}(?:e\d+)?|\d{1,2}x\d{1,2}|(?:season|сезон|епізод|серія|серия)[\s._-]*\d+)(?:$|[^\p{L}\d])");
    private static readonly Regex Packs = Rx(@"(?:^|[^\p{L}\d])(?:collection|anthology|duology|trilogy|tetralogy|колекція|коллекция|трилогія|трилогия)(?:$|[^\p{L}\d])");
    private static readonly Regex Extras = Rx(@"(?:^|[/\\])(?:samples?|trailers?|extras?|featurettes?|bonus)(?:[/\\]|\.(?:mkv|mp4|avi|mov|m4v|webm|ts|wmv)$)");
    private static readonly Regex Ukrainian = Rx(@"(?:^|[^\p{L}])(?:ukr|ua|ukrainian|українська|українською)(?:$|[^\p{L}])");
    private static readonly Regex Tech = Rx(@"(?:^|[\s._(\[-])(?:\d{3,4}[pi]|[248]k|web[ ._-]?dl|web[ ._-]?rip|blu[ ._-]?ray|bdrip|brrip|remux|h[ .]?26[45]|x26[45]|hevc|avc|dvdrip)(?:$|[^\p{L}\d])");
    private static readonly Regex Brackets = Rx(@"\[[^\]]*\]");
    private static Regex Rx(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    public static string? CheckResult(DiscoveryMovieDto movie, TorrentSearchResult release)
    {
        if (!release.IsFreeleech) return "not_freeleech";
        if (release.Seeders <= 0) return "no_seeders";
        var hasImdb = !string.IsNullOrWhiteSpace(release.ImdbId);
        var hasTmdb = release.TmdbId.HasValue;
        if ((hasImdb && !string.Equals(release.ImdbId, movie.ImdbId, StringComparison.OrdinalIgnoreCase)) ||
            (hasTmdb && release.TmdbId != movie.TmdbId)) return "id_conflict";
        if (!hasImdb && !hasTmdb) return "id_missing";
        if (Episodes.IsMatch(release.Title) || Packs.IsMatch(release.Title)) return "not_single_movie";
        return TitleMatches(movie, release.Title, requireYear: true) ? null : "title_or_year_mismatch";
    }

    public static string? CheckFiles(DiscoveryMovieDto movie, TorrentMetadata metadata, out TorrTorrentFile? main)
    {
        main = null;
        if (metadata.Files.Any(f => Episodes.IsMatch(f.Path) || Packs.IsMatch(f.Path))) return "not_single_movie";
        if (metadata.Files.Any(f => new[] { ".exe", ".scr", ".bat", ".cmd", ".com", ".lnk" }.Contains(Path.GetExtension(f.Path).ToLowerInvariant()))) return "unsafe_files";
        // ISO / disc images need their own verification; a misleading disc filename is insufficient evidence.
        var videos = metadata.Files.Where(f => EpisodeMatcher.IsVideoFile(f.Path) && !Extras.IsMatch(f.Path)).ToList();
        if (videos.Count != 1 || videos[0].Length <= 0 || Path.GetExtension(videos[0].Path).Equals(".iso", StringComparison.OrdinalIgnoreCase)) return "ambiguous_files";
        var file = videos[0];
        var name = Path.GetFileNameWithoutExtension(file.Path.Replace('\\', '/'));
        if (!TitleMatches(movie, name, requireYear: false)) return "file_title_mismatch";
        main = file;
        return null;
    }

    public static bool HasUkrainianAudioHint(TorrentSearchResult release) => Ukrainian.IsMatch(release.Title) &&
        !Regex.IsMatch(release.Title, @"(?:ukr|ua)[\s._-]*(?:sub|суб)|(?:sub|суб)[\s._-]*(?:ukr|ua)", RegexOptions.IgnoreCase, RegexTimeout);

    public static bool TitleMatches(DiscoveryMovieDto movie, string title, bool requireYear)
    {
        if (title.Length > 2048 || !movie.Year.HasValue) return false;
        var aliases = movie.Titles.Concat(new[] { movie.Title, movie.OriginalTitle ?? "" }).Select(Normalize).Where(t => t.Length > 0).ToHashSet(StringComparer.Ordinal);
        title = Brackets.Replace(title, " ");
        var year = movie.Year.Value.ToString(CultureInfo.InvariantCulture);
        var yearMatches = Regex.Matches(title, @"(?<!\d)" + year + @"(?!\d)", RegexOptions.CultureInvariant, RegexTimeout);
        foreach (Match yearMatch in yearMatches)
        {
            var prefix = title[..yearMatch.Index];
            if (prefix.Split(new[] { '/', '|' }).Any(p => aliases.Contains(Normalize(p))))
            {
                // Another year after the release year is a pack/remake conflict, not a quality hint.
                if (Regex.IsMatch(title[(yearMatch.Index + 4)..], @"(?<![\dx])(?:18|19|20)\d{2}(?![\dx])", RegexOptions.IgnoreCase, RegexTimeout)) return false;
                return true;
            }
        }
        if (requireYear) return false;
        var tech = Tech.Match(title);
        var bare = tech.Success ? title[..tech.Index] : title;
        return aliases.Contains(Normalize(bare));
    }

    private static string Normalize(string text) => Regex.Replace(text.Normalize(NormalizationForm.FormKC).ToLowerInvariant(), @"[^\p{L}\p{Nd}]+", " ", RegexOptions.CultureInvariant, RegexTimeout).Trim();
}
