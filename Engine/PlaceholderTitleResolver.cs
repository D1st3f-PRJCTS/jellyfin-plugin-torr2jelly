using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Torr2Jelly.Api.Dtos;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Torr2Jelly.Engine;

/// <summary>
/// Turns lines of free text ("Dune (2021)", "The Last of Us") into placeholders: looks each title up on TMDb, picks a
/// match only when it is unambiguous, fetches its external ids and creates the movie / series placeholder through the
/// same engine calls as <c>POST Placeholders</c>. One failing line never stops the others.
/// </summary>
public class PlaceholderTitleResolver
{
    public const int MaxLines = 200;
    private const int MaxCandidates = 3;
    private const int MaxLineLength = 300;
    private const string EnglishLanguage = "en-US";

    private static readonly Regex ParenYearRegex = new(@"^(?<title>.+?)\s*\(\s*(?<year>\d{4})\s*\)$", RegexOptions.Compiled);
    private static readonly Regex BareYearRegex = new(@"^(?<title>.+?)\s+(?<year>(?:18|19|20)\d{2})$", RegexOptions.Compiled);

    private readonly ILogger<PlaceholderTitleResolver> _logger;
    private readonly TmdbClient _tmdb;
    private readonly StrmEngine _engine;

    /// <summary>Pause between lines (TMDb allows about 40 requests per second; this stays far below).</summary>
    internal TimeSpan LineDelay { get; set; } = TimeSpan.FromMilliseconds(60);

    public PlaceholderTitleResolver(ILogger<PlaceholderTitleResolver> logger, TmdbClient tmdb, StrmEngine engine)
    {
        _logger = logger;
        _tmdb = tmdb;
        _engine = engine;
    }

    /// <summary>
    /// Resolves every non-empty line. Throws <see cref="TmdbException"/> when the key is missing or rejected (nothing
    /// useful can happen then); any other TMDb problem only fails the line it hit.
    /// </summary>
    public async Task<FromTitlesResponseDto> ResolveAsync(
        IReadOnlyList<string> lines,
        string? kind,
        string? category,
        CancellationToken ct)
    {
        if (!TmdbClient.HasStoredKey())
        {
            throw new TmdbException(TmdbClient.CodeKeyMissing);
        }

        var mode = (kind ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "movie" => "movie",
            "series" or "tv" or "show" => "series",
            _ => "auto"
        };

        category = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
        string? categoryType = null;
        if (category != null)
        {
            categoryType = _engine.GetCategoryType(category);
        }

        var response = new FromTitlesResponseDto();
        LibraryIdSnapshot? snapshot = null;
        var first = true;

        foreach (var raw in lines.Take(MaxLines))
        {
            var line = (raw ?? string.Empty).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            ct.ThrowIfCancellationRequested();

            if (!first && LineDelay > TimeSpan.Zero)
            {
                await Task.Delay(LineDelay, ct);
            }

            first = false;

            try
            {
                response.Results.Add(await ResolveLineAsync(
                    line, mode, category, categoryType, () => snapshot ??= _engine.BuildLibraryIdSnapshot(), ct));
            }
            catch (TmdbException ex) when (ex.Code == TmdbClient.CodeKeyInvalid || ex.Code == TmdbClient.CodeKeyMissing)
            {
                throw;
            }
            catch (TmdbException)
            {
                response.Results.Add(Error(line, TmdbClient.CodeFailed));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create a placeholder from a title line");
                response.Results.Add(Error(line, "failed"));
            }
        }

        return response;
    }

    private async Task<FromTitlesLineResultDto> ResolveLineAsync(
        string line,
        string mode,
        string? category,
        string? categoryType,
        Func<LibraryIdSnapshot> getSnapshot,
        CancellationToken ct)
    {
        if (line.Length > MaxLineLength)
        {
            return Error(line, "line_too_long");
        }

        if (category != null && categoryType == null)
        {
            return Error(line, PlaceholderResult.ReasonUnknownCategory);
        }

        // A category fixes the kind; an explicit request that contradicts it is refused per line.
        if (categoryType != null)
        {
            if (mode == "movie" && categoryType == "show")
            {
                return Error(line, PlaceholderResult.ReasonNotMovieCategory);
            }

            if (mode == "series" && categoryType == "movie")
            {
                return Error(line, PlaceholderResult.ReasonNotShowCategory);
            }

            mode = categoryType == "show" ? "series" : "movie";
        }

        var type = mode == "movie" ? "movie" : mode == "series" ? "tv" : "all";
        var (title, year, bareYear) = ParseLine(line);

        var outcome = await FindAsync(title, year, type, ct);
        if (outcome.Selected == null && outcome.Exact.Count == 0 && bareYear && year.HasValue)
        {
            // "Blade Runner 2049": the trailing number may be part of the title.
            var whole = await FindAsync(line, null, type, ct);
            if (whole.Selected != null || whole.Exact.Count > 0)
            {
                outcome = whole;
            }
        }

        if (outcome.Selected == null)
        {
            if (outcome.Candidates.Count == 0)
            {
                return new FromTitlesLineResultDto { Line = line, Status = "not_found" };
            }

            var snapshot = getSnapshot();
            var candidates = outcome.Candidates.Take(MaxCandidates).Select(c => c.Dto.Clone()).ToList();
            ApplyLibraryState(candidates, snapshot);
            return new FromTitlesLineResultDto { Line = line, Status = "ambiguous", Candidates = candidates };
        }

        var chosen = outcome.Selected;
        var dto = chosen.Dto;
        var isSeries = dto.Kind == "series";

        var categoryName = category ?? _engine.GetDefaultCategoryName(isSeries ? "show" : "movie");
        if (string.IsNullOrEmpty(categoryName))
        {
            return Error(line, PlaceholderResult.ReasonCategoryUnavailable, dto);
        }

        TmdbExternalIds ids;
        try
        {
            ids = await _tmdb.GetExternalIdsAsync(dto.Kind, dto.TmdbId, ct);
        }
        catch (TmdbException ex) when (ex.Code == TmdbClient.CodeFailed)
        {
            return Error(line, TmdbClient.CodeFailed, dto);
        }

        var folderTitle = await ChooseFolderTitleAsync(chosen, outcome.Language, ct);

        PlaceholderResult created = isSeries
            ? await _engine.CreateSeriesPlaceholderAsync(categoryName, folderTitle, dto.Year, ids.ImdbId, ids.TvdbId, dto.TmdbId, ct)
            : await _engine.CreateMoviePlaceholderAsync(categoryName, folderTitle, dto.Year, ids.ImdbId ?? string.Empty, dto.TmdbId, ct);

        return new FromTitlesLineResultDto
        {
            Line = line,
            Status = created.Status,
            Kind = dto.Kind,
            TmdbId = dto.TmdbId,
            Title = dto.Title,
            Year = dto.Year,
            Folder = created.Folder,
            Reason = created.Reason
        };
    }

    private sealed class FindOutcome
    {
        public TmdbMatch? Selected { get; init; }

        /// <summary>The matches with the requested title (and year, when given).</summary>
        public List<TmdbMatch> Exact { get; init; } = new();

        /// <summary>What to offer when nothing could be chosen automatically: the exact title matches first, then the rest.</summary>
        public List<TmdbMatch> Candidates { get; init; } = new();

        /// <summary>The language of the search that produced <see cref="Selected"/> / the candidates.</summary>
        public string Language { get; init; } = string.Empty;
    }

    // Searches in the configured language; when nothing matches by title and that language is not English, once more
    // in English (a title typed in English may differ from the localized name).
    private async Task<FindOutcome> FindAsync(string title, int? year, string type, CancellationToken ct)
    {
        var language = TmdbClient.ConfiguredLanguage();
        var outcome = await SearchAndPickAsync(title, year, type, language, ct);
        if (outcome.Selected != null || outcome.Exact.Count > 0 || string.Equals(language, EnglishLanguage, StringComparison.Ordinal))
        {
            return outcome;
        }

        var english = await SearchAndPickAsync(title, year, type, EnglishLanguage, ct);
        if (english.Selected != null || english.Exact.Count > 0 || outcome.Candidates.Count == 0)
        {
            return english;
        }

        return outcome;
    }

    private async Task<FindOutcome> SearchAndPickAsync(string title, int? year, string type, string language, CancellationToken ct)
    {
        var matches = await _tmdb.SearchAsync(title, type, year, language, ct);
        if (matches.Count == 0 && year.HasValue)
        {
            // The year filter may be what excluded it: look again without, but then only offer candidates.
            matches = await _tmdb.SearchAsync(title, type, null, language, ct);
        }

        var wanted = Normalize(title);
        var titleMatches = wanted.Length == 0
            ? new List<TmdbMatch>()
            : matches.Where(m => Normalize(m.Dto.Title) == wanted || Normalize(m.Dto.OriginalTitle) == wanted).ToList();
        var exact = year.HasValue ? titleMatches.Where(m => m.Dto.Year == year).ToList() : titleMatches;

        var candidates = titleMatches.Concat(matches.Except(titleMatches)).ToList();
        return new FindOutcome
        {
            Selected = exact.Count == 1 ? exact[0] : null,
            Exact = exact,
            Candidates = exact.Count > 1 ? exact : candidates,
            Language = language
        };
    }

    // The folder shows the original title when that is Latin-script, otherwise the English title (so folder names stay
    // Latin like the rest of the library). The localized title comes from Jellyfin's own metadata fetch via the ids.
    private async Task<string> ChooseFolderTitleAsync(TmdbMatch match, string searchLanguage, CancellationToken ct)
    {
        var dto = match.Dto;
        if (IsLatinTitle(dto.OriginalTitle))
        {
            return dto.OriginalTitle!;
        }

        if (string.Equals(searchLanguage, EnglishLanguage, StringComparison.Ordinal) && IsLatinTitle(dto.Title))
        {
            return dto.Title;
        }

        try
        {
            var english = await _tmdb.GetEnglishTitleAsync(dto.Kind, dto.TmdbId, ct);
            if (IsLatinTitle(english))
            {
                return english!;
            }
        }
        catch (TmdbException ex) when (ex.Code == TmdbClient.CodeFailed)
        {
            // fall through to what the search already returned
        }

        return !string.IsNullOrWhiteSpace(dto.OriginalTitle) ? dto.OriginalTitle : dto.Title;
    }

    /// <summary>Marks each result with whether (and as what) it already exists in the library.</summary>
    public static void ApplyLibraryState(IEnumerable<TmdbResultDto> results, LibraryIdSnapshot snapshot)
    {
        foreach (var result in results)
        {
            var state = snapshot.Lookup(result.Kind, result.TmdbId, null);
            result.InLibrary = state != null;
            result.InLibraryAs = state;
        }
    }

    internal static (string Title, int? Year, bool BareYear) ParseLine(string line)
    {
        var text = line.Trim();
        var paren = ParenYearRegex.Match(text);
        if (paren.Success && int.TryParse(paren.Groups["year"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var py) &&
            py is >= 1870 and <= 2200)
        {
            return (paren.Groups["title"].Value.Trim(), py, false);
        }

        var bare = BareYearRegex.Match(text);
        if (bare.Success && int.TryParse(bare.Groups["year"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var by) &&
            by <= DateTime.UtcNow.Year + 2)
        {
            return (bare.Groups["title"].Value.Trim(), by, true);
        }

        return (text, null, false);
    }

    /// <summary>Case-, punctuation- and diacritics-insensitive form of a title: only its letters and digits remain.</summary>
    internal static string Normalize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(title.Length);
        foreach (var c in title.Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }

    /// <summary>True when the title has letters and all of them are Latin (Basic Latin through Latin Extended, incl. accents).</summary>
    internal static bool IsLatinTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var letters = 0;
        foreach (var c in title)
        {
            if (!char.IsLetter(c))
            {
                continue;
            }

            letters++;
            if (c >= 0x250 && !(c >= 0x1E00 && c <= 0x1EFF))
            {
                return false;
            }
        }

        return letters > 0;
    }

    private static FromTitlesLineResultDto Error(string line, string reason, TmdbResultDto? dto = null) => new()
    {
        Line = line,
        Status = "error",
        Reason = reason,
        Kind = dto?.Kind,
        TmdbId = dto?.TmdbId ?? 0,
        Title = dto?.Title,
        Year = dto?.Year
    };
}
