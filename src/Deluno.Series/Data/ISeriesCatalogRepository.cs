using Deluno.Series.Contracts;

namespace Deluno.Series.Data;

public interface ISeriesCatalogRepository
{
    Task<SeriesListItem> AddAsync(CreateSeriesRequest request, CancellationToken cancellationToken);

    Task<SeriesListItem?> GetByIdAsync(string id, CancellationToken cancellationToken);

    Task<IReadOnlyList<SeriesListItem>> ListAsync(CancellationToken cancellationToken);

    Task<int> UpdateMonitoredAsync(IReadOnlyList<string> seriesIds, bool monitored, CancellationToken cancellationToken);

    Task<SeriesListItem?> UpdateMetadataAsync(
        string id,
        string? metadataProvider,
        string? metadataProviderId,
        string? originalTitle,
        string? overview,
        string? posterUrl,
        string? backdropUrl,
        double? rating,
        string? genres,
        string? externalUrl,
        string? imdbId,
        string? metadataJson,
        CancellationToken cancellationToken);

    Task<int> UpdateEpisodeMonitoredAsync(IReadOnlyList<string> episodeIds, bool monitored, CancellationToken cancellationToken);

    Task<SeriesWantedSummary> GetWantedSummaryAsync(CancellationToken cancellationToken);

    Task<SeriesInventorySummary> GetInventorySummaryAsync(CancellationToken cancellationToken);

    Task<SeriesInventoryDetail?> GetInventoryDetailAsync(string seriesId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SeriesSearchHistoryItem>> ListSearchHistoryAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<SeriesWantedItem>> ListEligibleWantedAsync(
        string libraryId,
        int take,
        DateTimeOffset now,
        bool ignoreRetryWindow,
        CancellationToken cancellationToken);

    Task<int> CountRetryDelayedWantedAsync(
        string libraryId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task EnsureWantedStateAsync(
        string seriesId,
        string libraryId,
        string wantedStatus,
        string wantedReason,
        bool hasFile,
        string? currentQuality,
        string? targetQuality,
        bool qualityCutoffMet,
        CancellationToken cancellationToken);

    Task<bool> ImportExistingAsync(
        string libraryId,
        string title,
        int? startYear,
        string wantedStatus,
        string wantedReason,
        string? currentQuality,
        string? targetQuality,
        bool qualityCutoffMet,
        bool unmonitorWhenCutoffMet,
        string? filePath,
        long? fileSizeBytes,
        IReadOnlyList<ImportedEpisodeItem>? episodes,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SeriesTrackedFileItem>> ListTrackedFilesAsync(
        string libraryId,
        CancellationToken cancellationToken);

    Task<bool> MarkTrackedFileMissingAsync(
        string seriesId,
        string? episodeId,
        string libraryId,
        string filePath,
        CancellationToken cancellationToken);

    Task RecordSearchAttemptAsync(
        string seriesId,
        string? episodeId,
        string libraryId,
        string triggerKind,
        string outcome,
        DateTimeOffset now,
        DateTimeOffset? nextEligibleSearchUtc,
        string? lastSearchResult,
        string? releaseName,
        string? indexerName,
        string? detailsJson,
        CancellationToken cancellationToken);

    Task<int> ReevaluateLibraryWantedStateAsync(
        string libraryId,
        string? cutoffQuality,
        bool upgradeUntilCutoff,
        bool upgradeUnknownItems,
        CancellationToken cancellationToken);

    Task<SeriesImportRecoverySummary> GetImportRecoverySummaryAsync(CancellationToken cancellationToken);

    Task<SeriesImportRecoveryCase> AddImportRecoveryCaseAsync(
        CreateSeriesImportRecoveryCaseRequest request,
        CancellationToken cancellationToken);

    Task<bool> DeleteImportRecoveryCaseAsync(string id, CancellationToken cancellationToken);
}
