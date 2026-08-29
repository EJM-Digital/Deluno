using Deluno.Contracts;
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
    /// <summary>
    /// Titles that have what the profile asked for. Named <c>Waiting</c> until
    /// #300 — the word the server set on a title that was finished, and that
    /// the front end described as "not searchable yet".
    /// </summary>
    int CoveredCount,
    /// <summary>Titles that are not out yet, so there is nothing to look for.</summary>
    int UpcomingCount,
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
    // Null means "leave what is there", enforced by COALESCE in the write:
    // a provider that does not answer for one of these must not blank what an
    // earlier one found. That is why they carry a default — a caller with
    // nothing to say about runtime should be able to say nothing.
    int? RuntimeMinutes = null,
    double? Popularity = null,
    int? VoteCount = null,
    /// <summary>
    /// Whether a show is still running. Meaningless for a film, and simply not
    /// supplied for one.
    /// </summary>
    string? Status = null,
    /// <summary>
    /// Who made it. A show has a network and a film has a studio: the same
    /// question, two columns, because that is how the providers answer it.
    /// </summary>
    string? MadeBy = null,
    /// <summary>
    /// The rating the certification board gave it — PG-13, TV-MA. Provider
    /// vocabulary, kept as the provider's own word rather than an enum,
    /// because the vocabulary differs by country and by media kind.
    /// </summary>
    string? Certification = null,
    /// <summary>What it belongs to, where it belongs to something — "The Nolan Collection".</summary>
    string? Collection = null,
    /// <summary>The language it was made in, as an ISO code.</summary>
    string? OriginalLanguage = null,
    /// <summary>
    /// The four scores, each on its own, so a shelf can be ordered by one of
    /// them. Empty when the provider sent none, which is not the same as four
    /// zeroes and must not be written as one.
    /// </summary>
    IReadOnlyList<MediaRatingFact>? Ratings = null,
    /// <summary>
    /// What it is about, beyond its genre. Stored as one comma-separated
    /// column the way genres are, because nothing joins on a keyword.
    /// </summary>
    string? Keywords = null);

/// <summary>
/// One source's score for one title, on the way to its own column.
/// </summary>
/// <param name="Votes">
/// How many people it is drawn from, where the provider says. Null for the
/// critic percentages, which arrive as a bare number — and a rating with twelve
/// votes is not a rating, which is the whole reason #319 wanted this beside the
/// score rather than behind it.
/// </param>
public sealed record MediaRatingFact(string Source, double? Score, int? Votes);

/// <summary>
/// What the container itself says. Any field may be <c>null</c>, meaning the
/// probe did not answer it — the write leaves what is there rather than
/// blanking a name-parsed value with a measurement that was never taken.
/// </summary>
public sealed record ProbedFileFacts(string? VideoCodec, string? AudioCodec, string? AudioChannels);

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
    DateTimeOffset UpdatedUtc,
    /// <summary>
    /// The search state Deluno holds for this title, from the one wanted-state
    /// row it speaks for.
    ///
    /// A detail page used to find this by searching the wanted summary — a list
    /// of the 25 most recently updated titles — for the one title it was already
    /// showing. Open the 26th and the page lost the library, the target quality
    /// and the cutoff, and quietly fell back to defaults. The same defect the
    /// grid had, on the screen that shows a single title.
    /// </summary>
    string? LibraryId = null,
    string? WantedStatus = null,
    string? WantedReason = null,
    string? CurrentQuality = null,
    string? TargetQuality = null,
    bool? QualityCutoffMet = null,
    DateTimeOffset? LastSearchUtc = null,
    DateTimeOffset? NextEligibleSearchUtc = null);

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

    /// <summary>
    /// Reads wanted rows for an explicit selection in SQL. This keeps bulk
    /// actions correct even when the selected titles are not in the bounded
    /// recent-summary window.
    /// </summary>
    Task<IReadOnlyList<MediaWantedItem>> ListWantedByIdsAsync(
        MediaKind kind,
        IReadOnlyList<string> mediaIds,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MediaWantedItem>> ListEligibleWantedAsync(
        MediaKind kind,
        string libraryId,
        int take,
        DateTimeOffset now,
        bool ignoreRetryWindow,
        CancellationToken cancellationToken,
        string? wantedStatus = null);

    Task<int> CountRetryDelayedWantedAsync(
        MediaKind kind,
        string libraryId,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        string? wantedStatus = null);

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

    /// <summary>
    /// Records that a title has been handed to a download client, or that it is
    /// no longer with one.
    ///
    /// <para>Both directions in one method on purpose. The status and the moment
    /// it was set have to move together — a status with no timestamp can never
    /// expire, and a timestamp with no status is read by nothing — and two
    /// methods is two chances to write one without the other.</para>
    ///
    /// <para>Clearing does <b>not</b> decide what the title becomes. It returns
    /// it to <c>missing</c>, which is the honest answer for a title with no file
    /// and no download in flight, and lets the ordinary cycle work out the rest.
    /// Guessing anything better here would be a second copy of the rung
    /// rules.</para>
    /// </summary>
    /// <summary>
    /// Titles that say they are downloading, and have said so for long enough
    /// that a dispatch should exist by now.
    ///
    /// <para><paramref name="settledBefore"/> is a grace period, not a timeout.
    /// A grab writes the status and the dispatch row in that order, so a title
    /// read in the instant between the two would look abandoned when it is
    /// perfectly healthy. Anything more recent than this is simply left
    /// alone.</para>
    /// </summary>
    Task<IReadOnlyList<string>> ListDownloadingAsync(
        MediaKind kind,
        DateTimeOffset settledBefore,
        CancellationToken cancellationToken);

    Task SetDownloadingAsync(
        MediaKind kind,
        string mediaId,
        bool downloading,
        DateTimeOffset now,
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

    /// <summary>
    /// What a probe read out of the file, onto the wanted-state row that holds
    /// it.
    ///
    /// <para>The codec, the audio and the channel layout are parsed from the
    /// release name, which carries them by convention and carries nothing at
    /// all once a library has been renamed. The subtitle scan already opens
    /// every file with ffprobe; this is the same probe's other answer, written
    /// rather than thrown away.</para>
    ///
    /// <para>Keyed by file path as well as media id, because a title can be
    /// held in two libraries and the probe read one of them.</para>
    /// </summary>
    Task UpdateProbedFileFactsAsync(
        MediaKind kind,
        string mediaId,
        string filePath,
        ProbedFileFacts facts,
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
