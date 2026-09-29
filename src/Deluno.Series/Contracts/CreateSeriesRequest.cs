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
    bool SearchForCutoffUnmetNow = false,
    /// <summary>See <see cref="Deluno.Series.Contracts.SeriesNumberingDetail.SeasonFolder"/> (#850). Null follows the global Media Management format, exactly today's behaviour.</summary>
    bool? SeasonFolder = null,
    /// <summary>See <see cref="Deluno.Series.Contracts.SeriesNumberingDetail.ListMonitorMode"/> (#848). Null means this series was not added from a list, or the list chose "All".</summary>
    string? ListMonitorMode = null,
    /// <summary>Tag names picked in the add dialog (#1038); written with the title before routing and the added notification.</summary>
    IReadOnlyList<string>? Tags = null);
