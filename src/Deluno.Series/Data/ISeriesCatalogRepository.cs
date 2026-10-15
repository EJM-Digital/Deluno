using Deluno.Media;
using MetadataSearchResult = Deluno.Integrations.Metadata.MetadataSearchResult;
using MetadataProviderIssue = Deluno.Integrations.Metadata.MetadataProviderIssue;
using MetadataIdentityConflict = Deluno.Integrations.Metadata.MetadataIdentityConflict;
using Deluno.Contracts;
using Deluno.Series.Contracts;
using Deluno.Quality.ReleasePreferences;
using Deluno.Recovery.Contracts;

namespace Deluno.Series.Data;

public interface ISeriesCatalogRepository : ISeriesImportRecoveryRetentionRepository, Deluno.Quality.IQualityRankSink
{
    Task<SeriesListItem> AddAsync(CreateSeriesRequest request, CancellationToken cancellationToken);

    Task<SeriesListItem?> GetByIdAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the selected TV numbering model and the alternate keys attached
    /// to its canonical episodes.
    /// </summary>
    Task<SeriesNumberingDetail?> GetNumberingAsync(
        string seriesId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Changes numbering metadata without changing canonical episode identity.
    /// Owner mappings are explicit and survive provider catalogue refreshes.
    /// </summary>
    Task<SeriesNumberingDetail?> UpdateNumberingAsync(
        string seriesId,
        UpdateSeriesNumberingRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Applies an automatic scene/absolute mapping (TheXEM or a similar
    /// numbering source) to the episodes it names. An owner mapping is never
    /// touched — the same protection <see cref="UpdateNumberingAsync"/> gives a
    /// manual edit against a later provider catalogue refresh. Returns how many
    /// episodes actually changed, so a scheduled pass can report something more
    /// useful than "ran".
    /// </summary>
    Task<int> ApplyAutomaticNumberingAsync(
        string seriesId,
        IReadOnlyList<SeriesEpisodeSceneMapping> mappings,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records what TMDb's episode-groups endpoint published for this title
    /// (#863): which orderings are actually available (for the show page's
    /// switcher), any other ordering TMDb offers that Deluno does not resolve
    /// episodes by yet, and each episode's DVD-order and absolute numbers.
    /// An owner mapping is never touched, the same protection
    /// <see cref="ApplyAutomaticNumberingAsync"/> gives TheXEM's corrections.
    /// A mapping's own null fields leave the corresponding column exactly as
    /// it was - this and <see cref="ApplyAutomaticNumberingAsync"/> refresh
    /// different columns from different sources and must not blank out
    /// whatever the other one already found. Returns how many episodes
    /// actually changed.
    /// </summary>
    Task<int> ApplyEpisodeGroupsAsync(
        string seriesId,
        IReadOnlyList<string> availableNumberingSchemes,
        IReadOnlyList<string> otherNumberingSchemeNames,
        IReadOnlyList<SeriesEpisodeGroupMapping> mappings,
        CancellationToken cancellationToken);

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

    Task<MetadataIdentityConflict?> FindMetadataIdentityConflictAsync(
        string excludeId,
        string title,
        int? startYear,
        string? imdbId,
        string metadataProvider,
        string metadataProviderId,
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
    /// Returns the cache keys used by poster and backdrop URLs still stored in
    /// this catalogue. Artwork maintenance uses this set as its safety fence
    /// before removing old files.
    /// </summary>
    Task<IReadOnlySet<string>> ListReferencedArtworkCacheKeysAsync(CancellationToken cancellationToken);

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

    Task<MetadataProviderIssue?> GetMetadataProviderIssueAsync(string id, CancellationToken cancellationToken);

    Task<bool> RecordMetadataProviderIssueAsync(
        string id,
        MetadataProviderIssue issue,
        CancellationToken cancellationToken);

    Task<MetadataProviderIssue?> AcknowledgeMetadataProviderIssueAsync(
        string id,
        CancellationToken cancellationToken);

    Task ClearMetadataProviderIssueAsync(string id, CancellationToken cancellationToken);

    Task<int> UpdateMonitoredAsync(IReadOnlyList<string> seriesIds, bool monitored, CancellationToken cancellationToken);

    /// <summary>
    /// Applies everything a provider answered with, in one call.
    ///
    /// <para><b>This exists because the long form has five callers and adding a
    /// field means finding all five.</b> It did not get found: <c>status</c> and
    /// <c>network</c> were wired through the endpoint's helper and the worker's
    /// refresh job kept its own fifteen positional arguments, so a scheduled
    /// refresh wrote everything except the two fields that had just been added.
    /// The rig refreshed six shows and the column stayed empty.</para>
    ///
    /// <para>One mapping from a provider result to a row, so the next field is a
    /// single edit rather than a search. The long form stays for the manual
    /// override path, which deliberately builds its own values and is the one
    /// caller that is not applying a provider's answer.</para>
    /// </summary>
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
    Task<SeriesListItem?> UpdateMetadataAsync(
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
    Task<SeriesListItem?> UpdateMetadataAsync(MediaMetadataUpdate update, CancellationToken cancellationToken);



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

    Task<SeriesInventoryDetail?> GetInventoryDetailAsync(string seriesId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SeriesUpcomingEpisodeItem>> ListUpcomingEpisodesAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int take,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SeriesSearchHistoryItem>> ListSearchHistoryAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The same rows as <see cref="ListSearchHistoryAsync"/>, but with a
    /// caller-chosen limit rather than the fixed 20 (#754) -- the combined
    /// Indexer History screen reads a much wider window before merging with
    /// movie history and its own RSS-sync/auth-failure events.
    /// </summary>
    Task<IReadOnlyList<SeriesSearchHistoryItem>> ListRecentSearchHistoryAsync(int limit, CancellationToken cancellationToken);

    /// <summary>One show's own searches, newest first, paged on the server (#1337).</summary>
    Task<IReadOnlyList<SeriesSearchHistoryItem>> ListTitleSearchHistoryAsync(string seriesId, int limit, int offset, CancellationToken cancellationToken);

    Task<IReadOnlyList<SeriesWantedItem>> ListEligibleWantedAsync(
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
        string? filePath,
        long? fileSizeBytes,
        IReadOnlyList<ImportedEpisodeItem>? episodes,
        CancellationToken cancellationToken,
        PreferenceEvaluationSnapshot? preferenceEvaluation = null,
        IReadOnlyList<ImportedEpisodeNumberingItem>? alternateEpisodes = null,
        IReadOnlyList<ImportedSeasonPackItem>? seasonPacks = null,
        IReadOnlyList<PreferenceEvaluationSnapshot>? preferenceEvaluations = null,
        string? seriesId = null,
        bool upgradeUntilCutoff = false,
        bool upgradeUnknownItems = false,
        string episodeImportSource = "download",
        string? resultNote = null);

    /// <summary>
    /// Imports a slice of already-on-disk shows, and the episodes detected
    /// with them, in one transaction; returns how many were newly created.
    /// The single-show overload above is a batch of one.
    /// </summary>
    Task<int> ImportExistingBatchAsync(
        string libraryId,
        IReadOnlyList<ExistingSeriesImportRequest> requests,
        CancellationToken cancellationToken);

    /// <summary>
    /// The quality Deluno recorded for each episode file it holds in this
    /// library, by path. See <c>IMovieCatalogRepository</c> for why a scan
    /// reads this before it reads a file name.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ListRecordedFileQualitiesAsync(
        string libraryId,
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

    /// <summary>
    /// File gone from disk outside Deluno: clears the file, sets Missing and
    /// unmonitors the episode (or show) in one transaction, matched by path (#1065).
    /// </summary>
    Task<bool> MarkTrackedFileGoneAsync(
        string seriesId,
        string? episodeId,
        string libraryId,
        string filePath,
        CancellationToken cancellationToken);

    /// <summary>
    /// One-off repair (#1174): a library file linked to several episodes of a show
    /// when its name says which one(s). Keeps the episode(s) the name gives, clears
    /// the wrong links (file, size, quality; wanted again as Missing), and returns
    /// what was cleared so the caller can write it to Activity. Safe to run again.
    /// </summary>
    Task<IReadOnlyList<WrongEpisodeFileLinkRepair>> RepairWrongEpisodeFileLinksAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<WrongEpisodeFileLinkRepair>>([]);

    /// <summary>Every episode that holds a file, with its show and library (#1173).</summary>
    Task<IReadOnlyList<EpisodeFileLink>> ListEpisodeFileLinksAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<EpisodeFileLink>>([]);

    /// <summary>
    /// Takes a wrong file off an episode (#1173): the file the episode holds came from a release that names another
    /// episode, so the link is cleared and the episode is Missing again and searchable at once. The file itself is
    /// not touched here; the caller has already moved it to the recycle bin. False when the episode no longer holds
    /// that file, which makes a repeat run a no-op.
    /// </summary>
    Task<bool> ClearMislabelledEpisodeFileAsync(string episodeId, string filePath, CancellationToken cancellationToken, string? reason = null)
        => Task.FromResult(false);

    /// <summary>
    /// Corrects the show-level file notes that name this file (the show's latest file, kept beside each episode's
    /// own): they move to another file an episode still holds, or are cleared. Never changes whether the show is
    /// monitored. Used after Deluno itself moves a file to the recycle bin, so nothing reads it as removed outside Deluno.
    /// </summary>
    Task<int> DetachSeriesFileRecordsAsync(string? seriesId, string filePath, CancellationToken cancellationToken)
        => Task.FromResult(0);

    /// <summary>Start-up heal: show-level file notes that point into a recycle bin folder are corrected.</summary>
    Task<int> ClearSeriesFileRecordsInRecycleBinAsync(CancellationToken cancellationToken)
        => Task.FromResult(0);

    /// <summary>
    /// A library check (#1174): an episode that shows no file while its own, correctly named
    /// file sits in its show's folder (and no episode of the show is linked to it) is linked to
    /// that file. Returns what was linked so the caller can say so in Activity. Safe to run again.
    /// </summary>
    Task<IReadOnlyList<RelinkedEpisodeFile>> RelinkEpisodesWithTheirOwnFilesAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<RelinkedEpisodeFile>>([]);

    /// <summary>
    /// <see cref="MarkTrackedFileGoneAsync"/> for many files in one transaction
    /// (#1070). One answer per item, in order.
    /// </summary>
    Task<IReadOnlyList<bool>> MarkTrackedFilesGoneAsync(
        IReadOnlyList<SeriesFileGoneRequest> items,
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

    /// <summary>
    /// The counts of every open case and the newest <paramref name="limit"/> of them.
    /// <paramref name="includeDetails"/> false leaves each case's details JSON out (#1081).
    /// </summary>
    Task<SeriesImportRecoverySummary> GetImportRecoverySummaryAsync(CancellationToken cancellationToken, int limit = 500, bool includeDetails = true);

    Task<SeriesImportRecoveryCase> AddImportRecoveryCaseAsync(
        CreateSeriesImportRecoveryCaseRequest request,
        CancellationToken cancellationToken);

    Task<bool> DeleteImportRecoveryCaseAsync(string id, CancellationToken cancellationToken);

    Task<SeriesImportRecoveryCase?> GetImportRecoveryCaseAsync(string id, CancellationToken cancellationToken);

    Task<SeriesImportRecoveryCase?> ResolveImportRecoveryCaseAsync(string id, string status, CancellationToken cancellationToken);

    /// <summary>
    /// Closes the open recovery cases a scope covers, with the reason in each
    /// case's history. Returns how many closed.
    ///
    /// <para>One download that moved past the problem its cases describe is one
    /// dispatch id (#559). A removed title is every dispatch it made and every
    /// release they named (#563).</para>
    /// </summary>
    Task<int> SettleImportRecoveryCasesAsync(ImportRecoveryCaseScope scope, string reason, CancellationToken cancellationToken);

    /// <summary>The same, naming each case it closed so the caller can say so in Activity (#1167).</summary>
    Task<IReadOnlyList<SettledRecoveryCase>> SettleImportRecoveryCasesListedAsync(ImportRecoveryCaseScope scope, string reason, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<SettledRecoveryCase>>([]);

    /// <summary>Closes the open download or import cases whose title now holds its file (#1167), with or without details.</summary>
    Task<IReadOnlyList<SettledRecoveryCase>> SettleCasesOfTitlesHoldingFilesAsync(CancellationToken cancellationToken);

    /// <summary>Of these ids, the ones that hold a file, and when it arrived (#1167).</summary>
    Task<TitleFileHolds> GetTitleFileHoldsAsync(IReadOnlyCollection<string> entityIds, CancellationToken cancellationToken);

    Task AddImportRecoveryEventAsync(string caseId, string eventKind, string message, string? metadataJson, CancellationToken cancellationToken);

    Task<SeriesWantedItem?> GetSeriesWantedStateAsync(
        string seriesId,
        string libraryId,
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

    /// <summary>The show an episode belongs to, or <c>null</c> if there is no such episode (#595).</summary>
    Task<string?> GetEpisodeSeriesIdAsync(string episodeId, CancellationToken cancellationToken);

    /// <summary>The season and episode number (and show) of up to 100 episodes, for labelling a download on the Dashboard (#1096).</summary>
    Task<IReadOnlyList<(string EpisodeId, string SeriesId, int SeasonNumber, int EpisodeNumber)>> ListEpisodeNumbersAsync(IReadOnlyList<string> episodeIds, CancellationToken cancellationToken);

    /// <summary>Delete a series and all its related data</summary>
    Task<bool> DeleteAsync(string seriesId, CancellationToken cancellationToken);

    /// <summary>Update quality profile for a series</summary>
    Task<bool> UpdateQualityProfileAsync(string seriesId, string? qualityProfileId, CancellationToken cancellationToken);

    /// <summary>The per-show quality profile <see cref="UpdateQualityProfileAsync"/> writes; null when the show follows its library (#1014).</summary>
    Task<string?> GetQualityProfileIdAsync(string seriesId, CancellationToken cancellationToken);

    /// <summary>
    /// Changes the series' "monitor new episodes" preset (#801) - whether an
    /// episode this series discovers on a future refresh comes out monitored.
    /// Can be changed at any time, not only at series add time.
    /// </summary>
    Task<bool> UpdateMonitorNewEpisodesAsync(string seriesId, bool monitorNewEpisodes, CancellationToken cancellationToken);

    /// <summary>
    /// Every series' seasons, with episode counts and monitor state, for the
    /// cross-series Season Pass screen (#791). One query across the whole TV
    /// library rather than one inventory fetch per series.
    /// </summary>
    Task<IReadOnlyList<SeriesSeasonPassItem>> ListSeasonPassAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Bulk-toggles episode monitoring at the season or series level, across
    /// as many series as the Season Pass screen's selection covers (#791).
    /// A scope with a null season number is the whole series.
    /// </summary>
    Task<int> SetSeasonPassMonitoringAsync(
        IReadOnlyList<SeasonPassScope> scopes,
        bool monitored,
        CancellationToken cancellationToken);

    /// <summary>Reassign wanted-state library mapping for a batch of series.</summary>
    Task<int> ReassignLibraryAsync(
        IReadOnlyList<string> seriesIds,
        string fromLibraryId,
        string toLibraryId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reassigns a batch of shows and, in the same transaction, records the new location of every episode file
    /// whose folder the caller has already moved, with every other stored path that follows it (#1327).
    /// </summary>
    Task<int> ReassignLibraryAsync(
        IReadOnlyList<string> seriesIds,
        string fromLibraryId,
        string toLibraryId,
        IReadOnlyList<(string EpisodeId, string PreviousFilePath, string NewFilePath)> movedEpisodeFiles,
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
        string? wantedStatus = null,
        CatalogueFilters? filters = null,
        // See IMediaStateRepository.ListEligibleWantedAsync: false only for a
        // person's own retry of one episode (#653).
        bool requireMonitored = true);

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
    /// Every tracked episode file path across all series. Lets a film's Files tab treat a folder that
    /// also holds an episode as shared (#1065).
    /// </summary>
    Task<IReadOnlyList<string>> ListAllEpisodeFilePathsAsync(CancellationToken cancellationToken);

    /// <summary>Get the installed file path for a specific episode.</summary>
    Task<string?> GetEpisodeFilePathAsync(
        string episodeId,
        CancellationToken cancellationToken);

    Task<long?> GetEpisodeFileSizeBytesAsync(
        string episodeId,
        CancellationToken cancellationToken);

    /// <summary>
    /// True when any series other than <paramref name="seriesId"/> has an
    /// episode file inside <paramref name="directory"/> (#1067) - the folder is
    /// then not this show's own and must not be renamed as if it were.
    /// </summary>
    Task<bool> OtherSeriesHaveFilesUnderAsync(string seriesId, string directory, CancellationToken cancellationToken);

    /// <summary>Every episode of this series that has a file, with its path (#788).</summary>
    Task<IReadOnlyList<(string EpisodeId, string FilePath)>> ListEpisodeFilePathsAsync(
        string seriesId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records an episode's file at its new location after the caller has
    /// physically moved it - matched by <paramref name="previousFilePath"/> so
    /// a row that changed since it was read is never overwritten (#788).
    /// </summary>
    Task<bool> UpdateEpisodeFileLocationAsync(
        string episodeId,
        string previousFilePath,
        string newFilePath,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records several episodes' new file locations in one transaction: either every row follows
    /// the folder or none does (#1067).
    /// </summary>
    Task UpdateEpisodeFileLocationsAsync(
        IReadOnlyList<(string EpisodeId, string PreviousFilePath, string NewFilePath)> changes,
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

    /// <summary>
    /// Episodes airing inside a window, ordered by air date.
    /// </summary>
    /// <param name="includeUnmonitored">
    /// Radarr/Sonarr's calendar hides unmonitored episodes unless asked
    /// otherwise (#786); <c>false</c> keeps that default.
    /// </param>
    /// <param name="tagNames">
    /// When given, only episodes of a show carrying at least one of these
    /// tags (#786).
    /// </param>
    Task<IReadOnlyList<SeriesCalendarEpisodeItem>> ListCalendarEpisodesAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int take,
        bool includeUnmonitored,
        IReadOnlyList<string>? tagNames,
        CancellationToken cancellationToken);

    /// <summary>
    /// One page of episodes in a single wanted state, with the count of all of
    /// them (#1022): <c>missing</c> (no file) or <c>upgrade</c> (a file below the
    /// quality cutoff). Most recently aired first. <paramref name="monitored"/>
    /// narrows to monitored or unmonitored episodes; null means both.
    /// </summary>
    Task<(IReadOnlyList<WantedEpisodeItem> Items, int Total)> ListWantedEpisodesPageAsync(
        string wantedStatus,
        bool? monitored,
        int skip,
        int take,
        CancellationToken cancellationToken);

    /// <summary>
    /// The Dashboard's library numbers: the shelf's facet counts over the whole
    /// catalogue plus the bytes tracked episode files take up, per library.
    /// Count-only.
    /// </summary>
    Task<MediaDashboardCounts> GetDashboardCountsAsync(CancellationToken cancellationToken);

    /// <summary>Per-day counts for the dashboard, straight from stored timestamps.</summary>
    Task<MediaDailyMetrics> GetDailyMetricsAsync(
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken);
}
