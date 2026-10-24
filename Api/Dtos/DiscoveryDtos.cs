namespace Jellyfin.Plugin.Torr2Jelly.Api.Dtos;

public sealed class DiscoveryMovieDto
{
    public int TmdbId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? OriginalTitle { get; set; }
    public int? Year { get; set; }
    public string? Overview { get; set; }
    public string? PosterUrl { get; set; }
    public string? BackdropUrl { get; set; }
    public string? ImdbId { get; set; }
    public double? TmdbRating { get; set; }
    public int TmdbVotes { get; set; }
    public double? ImdbRating { get; set; }
    public long? ImdbVotes { get; set; }
    public int? Runtime { get; set; }
    public bool IsAnimation { get; set; }
    public List<string> Genres { get; set; } = new();
    public List<DiscoveryActorDto> Actors { get; set; } = new();
    public List<string> Titles { get; set; } = new();
    public string? InLibraryAs { get; set; }
    public Guid? ItemId { get; set; }
    public Guid? ActiveJobId { get; set; }
}

public sealed record DiscoveryActorDto(string Name, string? Character, string? ImageUrl);

public sealed class DiscoveryAddResultDto
{
    // stream / placeholder / exists. A placeholder may carry a search failure, not a false "no releases".
    public string Status { get; set; } = "placeholder";
    public string Reason { get; set; } = "no_verified_release";
    public Guid? ItemId { get; set; }
    public string? ReleaseTitle { get; set; }
    public int? Seeders { get; set; }
    public long? Size { get; set; }
    public List<string> Rejections { get; set; } = new();
}

public sealed record DiscoveryJobDto(Guid JobId, int TmdbId, string Status, DiscoveryAddResultDto? Result, string? Code);
