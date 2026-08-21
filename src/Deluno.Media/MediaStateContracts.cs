namespace Deluno.Media;

public sealed record MediaWantedItem(
    string Id,
    string Title,
    int? Year,
    string? ImdbId,
    string LibraryId,
    string WantedStatus,
    string WantedReason,
    bool HasFile,
    string? CurrentQuality,
    string? TargetQuality,
    bool QualityCutoffMet,
    DateTimeOffset? MissingSinceUtc,
    DateTimeOffset? LastSearchUtc,
    DateTimeOffset? NextEligibleSearchUtc,
    string? LastSearchResult,
    bool PreventLowerQualityReplacements,
    int? LastQualityDeltaDecision,
    DateTimeOffset UpdatedUtc);

public sealed record MediaWantedSummary(
    int TotalWanted,
    int MissingCount,
    int UpgradeCount,
    int WaitingCount,
    IReadOnlyList<MediaWantedItem> RecentItems);

public sealed record MediaSearchHistoryItem(
    string Id,
    string MediaId,
    string? EpisodeId,
    int? SeasonNumber,
    int? EpisodeNumber,
    string LibraryId,
    string TriggerKind,
    string Outcome,
    string? ReleaseName,
    string? IndexerName,
    string? DetailsJson,
    DateTimeOffset CreatedUtc);

public sealed record MediaImportRecoveryCase(
    string Id,
    string Title,
    string FailureKind,
    string Status,
    string Summary,
    string RecommendedAction,
    string? DetailsJson,
    DateTimeOffset DetectedUtc,
    DateTimeOffset? ResolvedUtc);

public sealed record MediaImportRecoverySummary(
    int OpenCount,
    int QualityCount,
    int UnmatchedCount,
    int CorruptCount,
    int DownloadFailedCount,
    int ImportFailedCount,
    IReadOnlyList<MediaImportRecoveryCase> RecentCases);

public sealed record MediaMetadataUpdate(
    string Id,
    string? MetadataProvider,
    string? MetadataProviderId,
    string? OriginalTitle,
    string? Overview,
    string? PosterUrl,
    string? BackdropUrl,
    double? Rating,
    string? Genres,
    string? ExternalUrl,
    string? ImdbId,
    string? MetadataJson,
    int? RuntimeMinutes,
    double? Popularity,
    int? VoteCount);

public sealed record MediaEntryCreate(
    string Title,
    int? Year,
    string? ImdbId,
    bool Monitored,
    string? MetadataProvider,
    string? MetadataProviderId,
    string? OriginalTitle,
    string? Overview,
    string? PosterUrl,
    string? BackdropUrl,
    double? Rating,
    string? Genres,
    string? ExternalUrl,
    string? MetadataJson);

public sealed record MediaEntryDetails(
    string Id,
    string Title,
    int? Year,
    string? ImdbId,
    bool Monitored,
    bool HasFile,
    string? MetadataProvider,
    string? MetadataProviderId,
    string? OriginalTitle,
    string? Overview,
    string? PosterUrl,
    string? BackdropUrl,
    double? Rating,
    string? Genres,
    string? ExternalUrl,
    string? MetadataJson,
    DateTimeOffset? MetadataUpdatedUtc,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

public sealed record MediaExistingImportRequest(
    string Title,
    int? Year,
    string WantedStatus,
    string WantedReason,
    string? CurrentQuality,
    string? TargetQuality,
    bool QualityCutoffMet,
    bool UnmonitorWhenCutoffMet,
    string? FilePath,
    long? FileSizeBytes);

public sealed record MediaImportResult(string Id, bool Created);

public sealed record MediaTrackedFileItem(
    string MediaId,
    string LibraryId,
    string Title,
    int? Year,
    string FilePath,
    long? FileSizeBytes,
    DateTimeOffset? ImportedUtc,
    DateTimeOffset? LastVerifiedUtc);

public interface IMediaStateRepository
{
    Task<MediaWantedSummary> GetWantedSummaryAsync(MediaKind kind, CancellationToken cancellationToken);

    Task<IReadOnlyList<MediaWantedItem>> ListEligibleWantedAsync(
        MediaKind kind,
        string libraryId,
        int take,
        DateTimeOffset now,
        bool ignoreRetryWindow,
        CancellationToken cancellationToken);

    Task<int> CountRetryDelayedWantedAsync(
        MediaKind kind,
        string libraryId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task EnsureWantedStateAsync(
        MediaKind kind,
        string mediaId,
        string libraryId,
        string wantedStatus,
        string wantedReason,
        bool hasFile,
        string? currentQuality,
        string? targetQuality,
        bool qualityCutoffMet,
        CancellationToken cancellationToken);

    Task<bool> DeferWantedSearchAsync(
        MediaKind kind,
        string mediaId,
        string libraryId,
        DateTimeOffset deferredUntilUtc,
        CancellationToken cancellationToken);

    Task<bool> SkipNextWantedSearchAsync(
        MediaKind kind,
        string mediaId,
        string libraryId,
        CancellationToken cancellationToken);

    Task<bool> ConsumeSkipNextWantedSearchAsync(
        MediaKind kind,
        string mediaId,
        string libraryId,
        CancellationToken cancellationToken);

    Task<int> ReevaluateLibraryWantedStateAsync(
        MediaKind kind,
        string libraryId,
        string? cutoffQuality,
        bool upgradeUntilCutoff,
        bool upgradeUnknownItems,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MediaSearchHistoryItem>> ListSearchHistoryAsync(
        MediaKind kind,
        CancellationToken cancellationToken);

    Task<MediaImportRecoverySummary> GetImportRecoverySummaryAsync(
        MediaKind kind,
        CancellationToken cancellationToken);

    Task<bool> UpdateMetadataAsync(
        MediaKind kind,
        MediaMetadataUpdate update,
        CancellationToken cancellationToken);

    Task<string> AddAsync(
        MediaKind kind,
        MediaEntryCreate entry,
        CancellationToken cancellationToken);

    Task<MediaEntryDetails?> GetByIdAsync(
        MediaKind kind,
        string id,
        CancellationToken cancellationToken);

    Task<MediaImportResult> ImportExistingAsync(
        MediaKind kind,
        string libraryId,
        MediaExistingImportRequest request,
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        CancellationToken cancellationToken);

    IAsyncEnumerable<MediaTrackedFileItem> StreamTrackedFilesAsync(
        MediaKind kind,
        string libraryId,
        CancellationToken cancellationToken);

    Task<Deluno.Contracts.MediaDailyMetrics> GetDailyMetricsAsync(
        MediaKind kind,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken);
}
