using Deluno.Contracts;
using Deluno.Movies.Contracts;

namespace Deluno.Movies.Data;

public interface IMovieCatalogRepository
{
    Task<MovieListItem> AddAsync(CreateMovieRequest request, CancellationToken cancellationToken);

    Task<MovieListItem?> GetByIdAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// The id of the entry this request would land on, or <c>null</c> if it
    /// would create a new one — the same matching rules
    /// <see cref="AddAsync"/> applies, asked without adding anything.
    ///
    /// This exists so a caller can answer "do I already have this?" with an
    /// indexed lookup. Intake used to answer it by loading the entire catalogue
    /// into a dictionary every five minutes, which is the one shape that cannot
    /// survive a growing library.
    /// </summary>
    Task<string?> FindExistingIdAsync(
        string title,
        int? releaseYear,
        string? imdbId,
        string? metadataProvider,
        string? metadataProviderId,
        CancellationToken cancellationToken);

    /// <summary>
    /// One page of the catalogue — searched, filtered, sorted and counted in
    /// SQL. This is what a list surface should use; <see cref="ListAsync"/>
    /// returns the whole catalogue and does not survive a growing library.
    /// </summary>
    Task<CataloguePage<MovieListItem>> ListPageAsync(
        CatalogueQuery query,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MovieListItem>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The stalest movies still wanting metadata, filtered, ordered and
    /// limited in SQL. Returns only what a refresh job payload needs.
    /// </summary>
    /// <summary>
    /// How many entries the backfill would consider stale right now. The
    /// counterpart to <see cref="ListStaleMetadataCandidatesAsync"/>, so a
    /// caller that queues a page of them can say honestly how many are left
    /// rather than reporting the page as if it were the whole job.
    /// </summary>
    Task<int> CountStaleMetadataCandidatesAsync(
        DateTimeOffset staleBefore,
        DateTimeOffset retryAttemptsBefore,
        CancellationToken cancellationToken);

    /// <summary>
    /// Marks every entry as wanting a metadata refresh, and returns how many
    /// were marked.
    ///
    /// One statement, whatever the library size. It deliberately does not clear
    /// <c>metadata_updated_utc</c>: forcing a refresh should not destroy the
    /// record of when each entry was genuinely last refreshed, which is what
    /// the backfill prioritises by and what a user sees on a title.
    /// </summary>
    Task<int> RequestMetadataRefreshForAllAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<Deluno.Jobs.Contracts.MetadataRefreshCandidate>> ListStaleMetadataCandidatesAsync(
        DateTimeOffset staleBefore,
        DateTimeOffset retryAttemptsBefore,
        int take,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records that a metadata refresh was attempted, regardless of whether
    /// the provider matched. Distinct from the success timestamp so an
    /// unmatchable entry is not re-selected by every backfill pass.
    /// </summary>
    Task RecordMetadataAttemptAsync(string id, CancellationToken cancellationToken);

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
        CancellationToken cancellationToken,
        int? runtimeMinutes = null,
        double? popularity = null,
        int? voteCount = null);

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

    /// <summary>
    /// Imports a slice of already-on-disk titles in one transaction, and
    /// returns how many were newly created. The single-title overload above is
    /// a batch of one; an existing-library import at any real size uses this.
    /// </summary>
    Task<int> ImportExistingBatchAsync(
        string libraryId,
        IReadOnlyList<ExistingMovieImportRequest> requests,
        CancellationToken cancellationToken);

    IAsyncEnumerable<MovieTrackedFileItem> StreamTrackedFilesAsync(
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
