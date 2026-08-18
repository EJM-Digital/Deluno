using Deluno.Contracts;
using Deluno.Movies.Contracts;

namespace Deluno.Movies.Data;

public interface IMovieCatalogRepository
{
    Task<MovieListItem> AddAsync(CreateMovieRequest request, CancellationToken cancellationToken);

    Task<MovieListItem?> GetByIdAsync(string id, CancellationToken cancellationToken);

    Task<IReadOnlyList<MovieListItem>> ListAsync(CancellationToken cancellationToken);

    Task<int> UpdateMonitoredAsync(IReadOnlyList<string> movieIds, bool monitored, CancellationToken cancellationToken);

    Task<MovieListItem?> UpdateMetadataAsync(
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

    Task<MovieWantedSummary> GetWantedSummaryAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<MovieSearchHistoryItem>> ListSearchHistoryAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<MovieWantedItem>> ListEligibleWantedAsync(
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
        string movieId,
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
        int? releaseYear,
        string wantedStatus,
        string wantedReason,
        string? currentQuality,
        string? targetQuality,
        bool qualityCutoffMet,
        bool unmonitorWhenCutoffMet,
        string? filePath,
        long? fileSizeBytes,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MovieTrackedFileItem>> ListTrackedFilesAsync(
        string libraryId,
        CancellationToken cancellationToken);

    Task<bool> MarkTrackedFileMissingAsync(
        string movieId,
        string libraryId,
        string filePath,
        CancellationToken cancellationToken);

    Task RecordSearchAttemptAsync(
        string movieId,
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

    Task<bool> DeferWantedSearchAsync(
        string movieId,
        string libraryId,
        DateTimeOffset deferredUntilUtc,
        CancellationToken cancellationToken);

    /// <summary>Request that exactly one eligible background search is skipped. Manual searches are unaffected.</summary>
    Task<bool> SkipNextWantedSearchAsync(
        string movieId,
        string libraryId,
        CancellationToken cancellationToken);

    /// <summary>Atomically consumes a pending single background-search skip.</summary>
    Task<bool> ConsumeSkipNextWantedSearchAsync(
        string movieId,
        string libraryId,
        CancellationToken cancellationToken);

    Task<int> ReevaluateLibraryWantedStateAsync(
        string libraryId,
        string? cutoffQuality,
        bool upgradeUntilCutoff,
        bool upgradeUnknownItems,
        CancellationToken cancellationToken);

    Task<MovieImportRecoverySummary> GetImportRecoverySummaryAsync(CancellationToken cancellationToken);

    Task<MovieImportRecoveryCase> AddImportRecoveryCaseAsync(
        CreateMovieImportRecoveryCaseRequest request,
        CancellationToken cancellationToken);

    Task<bool> DeleteImportRecoveryCaseAsync(string id, CancellationToken cancellationToken);

    Task<MovieImportRecoveryCase?> ResolveImportRecoveryCaseAsync(string id, string status, CancellationToken cancellationToken);

    Task AddImportRecoveryEventAsync(string caseId, string eventKind, string message, string? metadataJson, CancellationToken cancellationToken);

    Task<int> CleanupImportRecoveryCasesAsync(DateTimeOffset olderThan, CancellationToken cancellationToken);

    Task<MovieWantedItem?> GetMovieWantedStateAsync(
        string movieId,
        string libraryId,
        CancellationToken cancellationToken);

    Task<bool> UpdateMovieReplacementPolicyAsync(
        string movieId,
        string libraryId,
        bool preventLowerQualityReplacements,
        CancellationToken cancellationToken);

    Task<bool> UpdateMovieQualityDeltaAsync(
        string movieId,
        string libraryId,
        int? qualityDelta,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CrossLibraryDuplicateItem>> FindCrossLibraryDuplicatesAsync(CancellationToken cancellationToken);

    Task<int> ReassignLibraryAsync(IReadOnlyList<string> movieIds, string fromLibraryId, string toLibraryId, CancellationToken cancellationToken);

    /// <summary>Delete a movie and all its related data</summary>
    Task<bool> DeleteAsync(string movieId, CancellationToken cancellationToken);

    /// <summary>Update quality profile for a movie</summary>
    Task<bool> UpdateQualityProfileAsync(string movieId, string qualityProfileId, CancellationToken cancellationToken);

    /// <summary>Store the provider's cinema, digital and physical release dates.</summary>
    Task<bool> UpdateReleaseDatesAsync(
        string movieId,
        DateOnly? inCinemas,
        DateOnly? digital,
        DateOnly? physical,
        CancellationToken cancellationToken);

    /// <summary>Set when Deluno may start searching: announced, inCinemas or released.</summary>
    Task<bool> UpdateMinimumAvailabilityAsync(
        string movieId,
        string minimumAvailability,
        CancellationToken cancellationToken);

    /// <summary>Films with a cinema, digital or physical release inside a window.</summary>
    Task<IReadOnlyList<MovieCalendarItem>> ListCalendarMoviesAsync(
        DateOnly fromDate,
        DateOnly toDate,
        int take,
        CancellationToken cancellationToken);

    /// <summary>Per-day counts for the dashboard, straight from stored timestamps.</summary>
    Task<MediaDailyMetrics> GetDailyMetricsAsync(
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken);
}
