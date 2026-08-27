using Deluno.Contracts;
using Deluno.Series.Contracts;
using Deluno.Recovery.Contracts;

namespace Deluno.Series.Data;

public interface ISeriesCatalogRepository : ISeriesImportRecoveryRetentionRepository
{
    Task<SeriesListItem> AddAsync(CreateSeriesRequest request, CancellationToken cancellationToken);

    Task<SeriesListItem?> GetByIdAsync(string id, CancellationToken cancellationToken);

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
        int? startYear,
        string? imdbId,
        string? metadataProvider,
        string? metadataProviderId,
        CancellationToken cancellationToken);

    /// <summary>
    /// One page of the catalogue — searched, filtered, sorted and counted in
    /// SQL. This is what a list surface should use; <see cref="ListAsync"/>
    /// returns the whole catalogue and does not survive a growing library.
    /// </summary>
    Task<CataloguePage<SeriesListItem>> ListPageAsync(
        CatalogueQuery query,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SeriesListItem>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The stalest series still wanting metadata, filtered, ordered and
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
        CancellationToken cancellationToken,
        int? runtimeMinutes = null,
        double? popularity = null,
        int? voteCount = null);

    Task<int> UpdateEpisodeMonitoredAsync(IReadOnlyList<string> episodeIds, bool monitored, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves the parent series for a bounded set of episode ids. Episode
    /// mutations invalidate series-shaped client data, so callers need the
    /// parent identities rather than exposing an EpisodeChanged event.
    /// </summary>
    Task<IReadOnlyList<string>> ListParentSeriesIdsAsync(
        IReadOnlyList<string> episodeIds,
        CancellationToken cancellationToken);

    Task<SeriesWantedSummary> GetWantedSummaryAsync(CancellationToken cancellationToken);

    Task<SeriesInventorySummary> GetInventorySummaryAsync(CancellationToken cancellationToken);

    Task<SeriesInventoryDetail?> GetInventoryDetailAsync(string seriesId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SeriesUpcomingEpisodeItem>> ListUpcomingEpisodesAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int take,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SeriesSearchHistoryItem>> ListSearchHistoryAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<SeriesWantedItem>> ListEligibleWantedAsync(
        string libraryId,
        int take,
        DateTimeOffset now,
        bool ignoreRetryWindow,
        CancellationToken cancellationToken,
        string? wantedStatus = null);

    Task<int> CountRetryDelayedWantedAsync(
        string libraryId,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        string? wantedStatus = null);

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

    /// <summary>
    /// Imports a slice of already-on-disk shows, and the episodes detected
    /// with them, in one transaction; returns how many were newly created.
    /// The single-show overload above is a batch of one.
    /// </summary>
    Task<int> ImportExistingBatchAsync(
        string libraryId,
        IReadOnlyList<ExistingSeriesImportRequest> requests,
        CancellationToken cancellationToken);

    IAsyncEnumerable<SeriesTrackedFileItem> StreamTrackedFilesAsync(
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

    Task<bool> DeferWantedSearchAsync(
        string seriesId,
        string libraryId,
        DateTimeOffset deferredUntilUtc,
        CancellationToken cancellationToken);

    /// <summary>Request that exactly one eligible background search is skipped. Manual searches are unaffected.</summary>
    Task<bool> SkipNextWantedSearchAsync(
        string seriesId,
        string libraryId,
        CancellationToken cancellationToken);

    /// <summary>Atomically consumes a pending single background-search skip.</summary>
    Task<bool> ConsumeSkipNextWantedSearchAsync(
        string seriesId,
        string libraryId,
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

    Task<SeriesImportRecoveryCase?> ResolveImportRecoveryCaseAsync(string id, string status, CancellationToken cancellationToken);

    Task AddImportRecoveryEventAsync(string caseId, string eventKind, string message, string? metadataJson, CancellationToken cancellationToken);

    Task<SeriesWantedItem?> GetSeriesWantedStateAsync(
        string seriesId,
        string libraryId,
        CancellationToken cancellationToken);

    Task<bool> UpdateSeriesReplacementPolicyAsync(
        string seriesId,
        string libraryId,
        bool preventLowerQualityReplacements,
        CancellationToken cancellationToken);

    /// <summary>
    /// Episodes in this library that have a file which is below the quality
    /// cutoff, oldest first, capped at <paramref name="perSeriesLimit"/> per
    /// show so one long-running series cannot fill the whole result.
    ///
    /// The filtering, the per-series cap and the overall cap are all in SQL. The
    /// previous shape asked for every series and then every episode of each of
    /// them, in order to return twenty ids.
    /// </summary>
    Task<IReadOnlyList<string>> ListEpisodesNeedingRecoveryAsync(
        string libraryId,
        int perSeriesLimit,
        int take,
        CancellationToken cancellationToken);

    /// <summary>
    /// When this episode last changed, or <c>null</c> if there is no such
    /// episode. One indexed lookup by id.
    /// </summary>
    Task<DateTimeOffset?> GetEpisodeUpdatedUtcAsync(string episodeId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SeriesEpisodeInventoryItem>> ListMonitoredMissingEpisodesAsync(
        string seriesId,
        string libraryId,
        CancellationToken cancellationToken);

    /// <summary>Delete a series and all its related data</summary>
    Task<bool> DeleteAsync(string seriesId, CancellationToken cancellationToken);

    /// <summary>Update quality profile for a series</summary>
    Task<bool> UpdateQualityProfileAsync(string seriesId, string qualityProfileId, CancellationToken cancellationToken);

    /// <summary>Reassign wanted-state library mapping for a batch of series.</summary>
    Task<int> ReassignLibraryAsync(
        IReadOnlyList<string> seriesIds,
        string fromLibraryId,
        string toLibraryId,
        CancellationToken cancellationToken);

    /// <summary>List episodes eligible for search in a library</summary>
    /// <summary>
    /// The episodes a library owes you: aired, monitored, still short, and past
    /// their retry window.
    ///
    /// Signature mirrors <see cref="ListEligibleWantedAsync"/> deliberately —
    /// the series pass and the episode pass are the same cycle seen at two
    /// levels, and a caller that gates one and not the other is how the two
    /// drift apart. <paramref name="ignoreRetryWindow"/> is what a manual
    /// "search now" sets; <paramref name="wantedStatus"/> narrows a
    /// missing-only or upgrade-only cycle to its own half.
    /// </summary>
    Task<IReadOnlyList<EpisodeSearchEligibilityItem>> ListEligibleWantedEpisodesAsync(
        string libraryId,
        int take,
        DateTimeOffset now,
        bool ignoreRetryWindow,
        CancellationToken cancellationToken,
        string? wantedStatus = null);

    /// <summary>Get target quality for a specific episode</summary>
    Task<string?> GetEpisodeTargetQualityAsync(
        string episodeId,
        string libraryId,
        CancellationToken cancellationToken);

    /// <summary>Get current quality for a specific episode</summary>
    Task<string?> GetEpisodeCurrentQualityAsync(
        string episodeId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Write the provider's season/episode catalogue over the series' inventory.
    /// Adds episodes nobody has a file for, fills in titles and air dates, and
    /// leaves every file-derived column alone.
    /// </summary>
    Task<SeriesCatalogueSyncResult> SyncEpisodeCatalogueAsync(
        string seriesId,
        IReadOnlyList<CatalogueEpisodeItem> episodes,
        string source,
        CancellationToken cancellationToken);

    /// <summary>Episodes airing inside a window, ordered by air date.</summary>
    Task<IReadOnlyList<SeriesCalendarEpisodeItem>> ListCalendarEpisodesAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int take,
        CancellationToken cancellationToken);

    /// <summary>Episodes still wanted across every series.</summary>
    Task<IReadOnlyList<WantedEpisodeItem>> ListWantedEpisodesAsync(
        int take,
        CancellationToken cancellationToken);

    /// <summary>Per-day counts for the dashboard, straight from stored timestamps.</summary>
    Task<MediaDailyMetrics> GetDailyMetricsAsync(
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken);
}
