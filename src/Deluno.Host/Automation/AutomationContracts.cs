namespace Deluno.Host.Automation;

public static class AutomationBatchLimits
{
    public const int MaxCatalogueItems = 250;
    public const int MaxEpisodeItems = 1_000;
    public const int MaxIdempotencyKeyLength = 200;
}

/// <summary>
/// The supported automation write shape. One request may contain movies and TV
/// shows; each item is still evaluated and reported independently.
/// </summary>
public sealed record BulkCatalogueAddRequest(
    IReadOnlyList<BulkCatalogueAddItem>? Items,
    bool DryRun = false,
    string? IdempotencyKey = null);

public sealed record BulkCatalogueAddItem(
    string? ClientItemId,
    string? MediaType,
    string? Title,
    int? Year,
    string? ImdbId,
    string? LibraryId = null,
    bool Monitored = true,
    bool IsReleased = true,
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
    string? SeriesType = null,
    string? NumberingScheme = null,
    string? NumberingSource = null,
    IReadOnlyList<BulkCatalogueEpisode>? Episodes = null);

public sealed record BulkCatalogueEpisode(
    int SeasonNumber,
    int EpisodeNumber,
    string? Title = null,
    string? Overview = null,
    DateTimeOffset? AirDateUtc = null,
    int? AbsoluteNumber = null,
    int? SceneSeasonNumber = null,
    int? SceneEpisodeNumber = null,
    string? NumberingSource = null);

public sealed record BulkCatalogueItemResult(
    string ClientItemId,
    string MediaType,
    string? Title,
    string Status,
    string? EntityId = null,
    string? Error = null,
    int EpisodeCount = 0,
    int EpisodesAdded = 0,
    int EpisodesUpdated = 0,
    string? RefreshJobId = null);

public sealed record BulkCatalogueAddResponse(
    bool DryRun,
    string? IdempotencyKey,
    int Total,
    int CreatedCount,
    int ExistingCount,
    int InvalidCount,
    int FailedCount,
    IReadOnlyList<BulkCatalogueItemResult> Items);

/// <summary>
/// Explicit episode catalogue input for callers that already know a series
/// identity and do not need to submit the parent show again.
/// </summary>
public sealed record BulkSeriesEpisodeRequest(
    IReadOnlyList<BulkSeriesEpisodeItem>? Episodes,
    bool DryRun = false,
    string? IdempotencyKey = null);

public sealed record BulkSeriesEpisodeItem(
    string? ClientItemId,
    int SeasonNumber,
    int EpisodeNumber,
    string? Title = null,
    string? Overview = null,
    DateTimeOffset? AirDateUtc = null,
    int? AbsoluteNumber = null,
    int? SceneSeasonNumber = null,
    int? SceneEpisodeNumber = null,
    string? NumberingSource = null);

public sealed record BulkSeriesEpisodeItemResult(
    string ClientItemId,
    int SeasonNumber,
    int EpisodeNumber,
    string Status,
    string? Error = null);

public sealed record BulkSeriesEpisodeResponse(
    bool DryRun,
    string? IdempotencyKey,
    string SeriesId,
    int Total,
    int SyncedCount,
    int InvalidCount,
    int FailedCount,
    int EpisodesAdded,
    int EpisodesUpdated,
    IReadOnlyList<BulkSeriesEpisodeItemResult> Episodes);

public sealed record AutomationSummaryResponse(
    DateTimeOffset GeneratedUtc,
    AutomationReadinessSummary Readiness,
    AutomationQueueSummary Queue,
    AutomationImportSummary Imports,
    IReadOnlyList<AutomationAttentionItem> Attention);

public sealed record AutomationReadinessSummary(
    string Status,
    bool Ready,
    int FailedChecks);

public sealed record AutomationQueueSummary(
    int Active,
    int Queued,
    int Failed,
    int OpenDispatchAlerts);

public sealed record AutomationImportSummary(
    int Active,
    int Failed,
    int Completed,
    int Issues);

public sealed record AutomationAttentionItem(
    string Code,
    string Severity,
    string Summary,
    string Details,
    DateTimeOffset DetectedUtc);
