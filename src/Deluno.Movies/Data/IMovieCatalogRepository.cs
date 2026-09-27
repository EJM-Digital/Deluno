using Deluno.Media;
using MetadataSearchResult = Deluno.Integrations.Metadata.MetadataSearchResult;
using MetadataProviderIssue = Deluno.Integrations.Metadata.MetadataProviderIssue;
using MetadataIdentityConflict = Deluno.Integrations.Metadata.MetadataIdentityConflict;
using Deluno.Contracts;
using Deluno.Movies.Contracts;
using Deluno.Quality.ReleasePreferences;
using Deluno.Recovery.Contracts;

namespace Deluno.Movies.Data;

public interface IMovieCatalogRepository : IMovieImportRecoveryRetentionRepository, Deluno.Quality.IQualityRankSink
{
    Task<MovieListItem> AddAsync(CreateMovieRequest request, CancellationToken cancellationToken);

    Task<MovieListItem?> GetByIdAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Every genre this catalogue actually holds, in alphabetical order.
    ///
    /// Served rather than derived in the browser from whatever happens to be on
    /// the current page: a genre filter that only offers the genres visible in
    /// the first fifty titles is a filter that hides the rest of the library
    /// from you and never says so.
    ///
    /// One pass over one column, run when somebody opens the filter panel — not
    /// on every page — because there is no index that can answer "distinct
    /// values inside a comma-separated string" and pretending otherwise would
    /// mean storing genres twice.
    /// </summary>
    Task<IReadOnlyList<string>> ListGenresAsync(CancellationToken cancellationToken);


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

    Task<MetadataIdentityConflict?> FindMetadataIdentityConflictAsync(
        string excludeId,
        string title,
        int? releaseYear,
        string? imdbId,
        string metadataProvider,
        string metadataProviderId,
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
    /// Returns the cache keys used by poster and backdrop URLs still stored in
    /// this catalogue. Artwork maintenance uses this set as its safety fence
    /// before removing old files.
    /// </summary>
    Task<IReadOnlySet<string>> ListReferencedArtworkCacheKeysAsync(CancellationToken cancellationToken);

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

    Task<MetadataProviderIssue?> GetMetadataProviderIssueAsync(string id, CancellationToken cancellationToken);

    Task<bool> RecordMetadataProviderIssueAsync(
        string id,
        MetadataProviderIssue issue,
        CancellationToken cancellationToken);

    Task<MetadataProviderIssue?> AcknowledgeMetadataProviderIssueAsync(
        string id,
        CancellationToken cancellationToken);

    Task ClearMetadataProviderIssueAsync(string id, CancellationToken cancellationToken);

    Task<int> UpdateMonitoredAsync(IReadOnlyList<string> movieIds, bool monitored, CancellationToken cancellationToken);

    /// <summary>
    /// The only way metadata reaches this catalogue.
    ///
    /// <para>There was a second, positional overload with eighteen parameters,
    /// five of them optional and trailing. Every field added to the provider
    /// result meant another optional parameter and another edit at every call
    /// site, and a call site that was not updated kept compiling and kept
    /// writing null. That is how <c>status</c> survived four attempts at being
    /// persisted and how <c>network</c> had no writer at all for four schema
    /// versions. Taking the provider's own record removes the failure: there is
    /// nothing to forget to pass.</para>
    /// </summary>
    Task<MovieListItem?> UpdateMetadataAsync(
        string id,
        MetadataSearchResult metadata,
        CancellationToken cancellationToken);

    /// <summary>
    /// Write metadata that did not come from a provider — a manual override.
    ///
    /// <para>Named fields on a record, because the alternative was eighteen
    /// positional arguments where the twelfth and the thirteenth were both
    /// nullable strings. A caller that passed them in the wrong order got a
    /// library with its overview in the genres column and no error anywhere.</para>
    /// </summary>
    Task<MovieListItem?> UpdateMetadataAsync(MediaMetadataUpdate update, CancellationToken cancellationToken);



    Task<MovieWantedSummary> GetWantedSummaryAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<MovieSearchHistoryItem>> ListSearchHistoryAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The same rows as <see cref="ListSearchHistoryAsync"/>, but with a
    /// caller-chosen limit rather than the fixed 20 (#754) -- the combined
    /// Indexer History screen reads a much wider window before merging with
    /// series history and its own RSS-sync/auth-failure events.
    /// </summary>
    Task<IReadOnlyList<MovieSearchHistoryItem>> ListRecentSearchHistoryAsync(int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<MovieWantedItem>> ListEligibleWantedAsync(
        string libraryId,
        int take,
        DateTimeOffset now,
        bool ignoreRetryWindow,
        CancellationToken cancellationToken,
        string? wantedStatus = null,
        CatalogueFilters? filters = null,
        // See IMediaStateRepository.ListEligibleWantedAsync: false only for a
        // person's own retry of one title (#653).
        bool requireMonitored = true,
        // Shuffles the tiebreak order within each status tier instead of
        // oldest-searched-first, so a recently added title is never stuck
        // behind a long backlog (#831).
        bool randomOrder = false);

    Task<int> CountRetryDelayedWantedAsync(
        string libraryId,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        string? wantedStatus = null);

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
        string? filePath,
        long? fileSizeBytes,
        CancellationToken cancellationToken,
        PreferenceEvaluationSnapshot? preferenceEvaluation = null,
        string? movieId = null);

    /// <summary>
    /// Imports a slice of already-on-disk titles in one transaction, and
    /// returns how many were newly created. The single-title overload above is
    /// a batch of one; an existing-library import at any real size uses this.
    /// </summary>
    Task<int> ImportExistingBatchAsync(
        string libraryId,
        IReadOnlyList<ExistingMovieImportRequest> requests,
        CancellationToken cancellationToken);

    /// <summary>
    /// The quality Deluno recorded for each file it holds in this library, by
    /// path. A scan reads this before it reads a file name: an owner who names
    /// files "Title (Year)" has not thrown the decision away, and Deluno must
    /// not pretend they have.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ListRecordedFileQualitiesAsync(
        string libraryId,
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

    Task<MovieImportRecoveryCase?> GetImportRecoveryCaseAsync(string id, CancellationToken cancellationToken);

    Task<MovieImportRecoveryCase?> ResolveImportRecoveryCaseAsync(string id, string status, CancellationToken cancellationToken);

    /// <summary>
    /// Closes the open recovery cases a scope covers, with the reason in each
    /// case's history. Returns how many closed.
    ///
    /// <para>One download that moved past the problem its cases describe is one
    /// dispatch id (#559). A removed title is every dispatch it made and every
    /// release they named (#563).</para>
    /// </summary>
    Task<int> SettleImportRecoveryCasesAsync(ImportRecoveryCaseScope scope, string reason, CancellationToken cancellationToken);

    Task AddImportRecoveryEventAsync(string caseId, string eventKind, string message, string? metadataJson, CancellationToken cancellationToken);

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

    /// <summary>
    /// Two catalogue rows that are the same film — matched on IMDb id, or on
    /// title and year where an id is absent, which is exactly the case that
    /// produces them.
    /// </summary>
    Task<IReadOnlyList<DuplicateTitleGroup>> FindDuplicateTitlesAsync(CancellationToken cancellationToken);

    Task<int> ReassignLibraryAsync(IReadOnlyList<string> movieIds, string fromLibraryId, string toLibraryId, CancellationToken cancellationToken);

    /// <summary>
    /// Reassigns a selection to another library/root and, for the ids present
    /// in <paramref name="movedFilePathsByMovieId"/>, records the new on-disk
    /// path in the same row — the caller has already moved the file itself
    /// before calling this, so the database and the disk agree the moment this
    /// returns. A movie with no entry in the map keeps its stored path;
    /// callers only pass one in when they actually moved that movie's file
    /// (#787).
    /// </summary>
    Task<int> ReassignLibraryAsync(
        IReadOnlyList<string> movieIds,
        string fromLibraryId,
        string toLibraryId,
        IReadOnlyDictionary<string, string>? movedFilePathsByMovieId,
        CancellationToken cancellationToken);

    /// <summary>Delete a movie and all its related data</summary>
    Task<bool> DeleteAsync(string movieId, CancellationToken cancellationToken);

    /// <summary>Update quality profile for a movie</summary>
    Task<bool> UpdateQualityProfileAsync(string movieId, string qualityProfileId, CancellationToken cancellationToken);

    /// <summary>
    /// The per-title quality profile override <see cref="UpdateQualityProfileAsync"/>
    /// writes, read back directly rather than through <c>GetByIdAsync</c>'s two
    /// diverging branches (#784).
    /// </summary>
    Task<string?> GetQualityProfileIdAsync(string movieId, CancellationToken cancellationToken);

    /// <summary>
    /// A per-file edit action on the movie's tracked (primary) file (#782) -
    /// there was previously no way to correct the detected quality by hand.
    /// </summary>
    Task<bool> UpdateCurrentQualityOverrideAsync(string movieId, string quality, CancellationToken cancellationToken);

    /// <summary>
    /// A per-file delete action removed the movie's tracked file from disk
    /// (#782). Matched by <paramref name="filePath"/> so a row that changed
    /// since it was read is never cleared by mistake.
    /// </summary>
    Task<bool> ClearFileAsync(string movieId, string filePath, CancellationToken cancellationToken);

    /// <summary>
    /// Records a file's new on-disk location after the caller has physically
    /// renamed or moved it - matched by <paramref name="previousFilePath"/>
    /// rather than movie id alone, so a stale or already-changed row is never
    /// overwritten with a path that does not belong to it (#788).
    /// </summary>
    Task<bool> UpdateFileLocationAsync(
        string movieId,
        string previousFilePath,
        string newFilePath,
        CancellationToken cancellationToken);

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

    /// <summary>
    /// Movies with a cinema, digital or physical release inside a window.
    /// </summary>
    /// <param name="includeUnmonitored">
    /// Radarr's calendar hides unmonitored titles unless asked otherwise (#786);
    /// <c>false</c> keeps that default.
    /// </param>
    /// <param name="tagNames">
    /// When given, only movies carrying at least one of these tags. Matched by
    /// name, the same way a saved filter condition matches a tag (#786).
    /// </param>
    Task<IReadOnlyList<MovieCalendarItem>> ListCalendarMoviesAsync(
        DateOnly fromDate,
        DateOnly toDate,
        int take,
        bool includeUnmonitored,
        IReadOnlyList<string>? tagNames,
        CancellationToken cancellationToken);

    /// <summary>Per-day counts for the dashboard, straight from stored timestamps.</summary>
    Task<MediaDailyMetrics> GetDailyMetricsAsync(
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken);
}
