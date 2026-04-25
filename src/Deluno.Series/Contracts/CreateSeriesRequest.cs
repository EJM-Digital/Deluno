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
    string? MetadataJson = null);
