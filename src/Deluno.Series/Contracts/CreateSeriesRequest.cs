namespace Deluno.Series.Contracts;

public sealed record CreateSeriesRequest(
    string? Title,
    int? StartYear,
    string? ImdbId,
    bool Monitored = true,
    string? MetadataProvider = null,
    string? MetadataProviderId = null,
    string? OriginalTitle = null,
    string? Overview = null,
    string? PosterUrl = null,
    string? BackdropUrl = null,
    double? Rating = null,
    string? Genres = null,
    string? ExternalUrl = null,
    string? MetadataJson = null,
    string? SeriesType = SeriesTypes.Standard,
    string? NumberingScheme = null,
    string? NumberingSource = "provider",
    string? LibraryId = null,
    /// <summary>Search for this show right away if it comes out missing (#793), regardless of Monitored.</summary>
    bool SearchForMissingNow = false,
    /// <summary>Search for this show right away if it comes out cutoff-unmet (#793), regardless of Monitored.</summary>
    bool SearchForCutoffUnmetNow = false);
