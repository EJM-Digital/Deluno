namespace Deluno.Worker.Dashboard;

/// <summary>
/// The one response behind the Dashboard (#1082): every panel's at-a-glance
/// numbers, assembled only from stored rows and in-memory readings, so the web
/// asks for one thing instead of two dozen (#1081).
///
/// <para>Every section is nullable. A section whose source failed or ran past
/// its time budget is <c>null</c> and named in <see cref="UnavailableSections"/>;
/// the others are unaffected. An empty install answers with plain zeros, empty
/// lists and nulls for things that have never been measured, never an error.
/// Every section carries its own <c>asOfUtc</c>, because a cached reading
/// (storage, the download queue) is older than the response around it.</para>
/// </summary>
public sealed record DashboardSummary(
    DateTimeOffset AsOfUtc,
    int BuildMilliseconds,
    IReadOnlyList<string> UnavailableSections,
    DashboardLibrarySection? Library,
    DashboardDownloadsSection? Downloads,
    DashboardPipelineSection? Pipeline,
    DashboardImportsSection? Imports,
    DashboardAutomationSection? Automation,
    DashboardIndexersSection? Indexers,
    DashboardProcessorsSection? Processors,
    DashboardRetentionSection? Retention,
    DashboardSubtitlesSection? Subtitles,
    DashboardRequestsSection? Requests,
    DashboardBlocklistSection? Blocklist,
    DashboardHealthSection? Health,
    DashboardStorageSection? Storage,
    DashboardSystemSection? System,
    DashboardActivitySection? Activity);

// ---------------------------------------------------------------- library

public sealed record DashboardLibrarySection(
    DateTimeOffset AsOfUtc,
    DashboardMediaCounts Movies,
    DashboardMediaCounts Shows,
    long TotalBytes,
    IReadOnlyList<DashboardLibraryBytes> Libraries,
    DashboardLibraryMix Mix,
    DashboardCollections Collections,
    IReadOnlyList<DashboardLibraryCounts>? LibraryCounts = null);

/// <summary>
/// What the files on disk are, biggest first. An empty label in the stored data
/// is reported as "Unknown"; <c>Count</c> is titles for films and episodes for shows
/// (the video codec is stored per title, so a show's codec counts shows).
/// </summary>
public sealed record DashboardLibraryMix(
    IReadOnlyList<DashboardMixEntry> MovieQuality,
    IReadOnlyList<DashboardMixEntry> MovieCodec,
    IReadOnlyList<DashboardMixEntry> EpisodeQuality,
    IReadOnlyList<DashboardMixEntry> ShowCodec);

public sealed record DashboardMixEntry(string Label, int Count);

/// <summary>Followed film collections: how many, how complete, and whether the last refresh failed.</summary>
public sealed record DashboardCollections(
    int Followed,
    int Monitored,
    int Members,
    int InLibrary,
    int SyncFailing,
    DateTimeOffset? LastSyncedUtc);

/// <summary>The shelf's own facet counts, whole catalogue, each title under one rung.</summary>
public sealed record DashboardMediaCounts(
    int Total,
    int Monitored,
    int Unmonitored,
    int Missing,
    int Upgradable,
    int QualityMet,
    int Upcoming,
    int Downloading,
    int Airing,
    long FileBytes);

public sealed record DashboardLibraryBytes(string Id, string Name, string MediaType, long Bytes);

/// <summary>One library's own counts, in the same words as its page (#1103): quality met, upgradable, downloading, missing, and what arrived in it today. In Settings › Libraries order.</summary>
public sealed record DashboardLibraryCounts(
    string LibraryId,
    string LibraryName,
    string MediaType,
    int Met,
    int Upgradable,
    int Downloading,
    int Missing,
    int ArrivedToday);

// -------------------------------------------------------------- downloads

/// <param name="HasReading">False until a download-client reading exists; the counts are then zero and mean "not measured", not "idle".</param>
/// <param name="ReadingUtc">When the client queue was last read. The dashboard never calls a client itself.</param>
public sealed record DashboardDownloadsSection(
    DateTimeOffset AsOfUtc,
    bool HasReading,
    DateTimeOffset? ReadingUtc,
    int Active,
    int Queued,
    int Stalled,
    int Processing,
    int ImportReady,
    int WaitingForProcessor,
    double DownloadMbps,
    double UploadMbps,
    IReadOnlyList<DashboardClientRow> Clients,
    int SharingHolds,
    long SharingExtraBytes,
    DashboardLeftovers Leftovers,
    int FailedDownloads);

/// <param name="Reachable">From the last reading when there is one, otherwise from the stored health of the connection.</param>
/// <param name="Paused">The client's own pause flag; null when its protocol has none or nothing has been read yet.</param>
public sealed record DashboardClientRow(
    string Id,
    string Name,
    string Protocol,
    bool Reachable,
    bool? Paused,
    string? Version,
    int Warnings,
    long? FreeSpaceBytes,
    string HealthStatus,
    int Active,
    int Queued);

/// <param name="Scanned">False until a leftover scan has run, when the count is unknown rather than zero.</param>
public sealed record DashboardLeftovers(bool Scanned, int Count, long Bytes, DateTimeOffset? ScannedUtc);

// --------------------------------------------------------------- pipeline

public sealed record DashboardPipelineSection(
    DateTimeOffset AsOfUtc,
    DashboardPipelineDay Today,
    DashboardPipelineTotals Totals,
    double? AverageGrabToDetectionSeconds,
    double? AverageDetectionToImportSeconds);

public sealed record DashboardPipelineDay(int Grabbed, int Detected, int Imported, int Failed);

public sealed record DashboardPipelineTotals(
    long Grabbed,
    long Detected,
    long Imported,
    long FailedGrabs,
    long FailedImports,
    int ActiveDispatches);

// ---------------------------------------------------------------- imports

public sealed record DashboardImportsSection(
    DateTimeOffset AsOfUtc,
    int RecoveryOpen,
    IReadOnlyDictionary<string, int> RecoveryByKind,
    int FailedImportJobs,
    int FailedProcessorHandoffs);

// ------------------------------------------------------------- automation

public sealed record DashboardAutomationSection(
    DateTimeOffset AsOfUtc,
    bool AutoStartJobs,
    DashboardQueuePauseGate QueuePause,
    IReadOnlyList<DashboardLibraryAutomation> Libraries,
    DashboardSearchCycle? LastSearchCycle,
    IReadOnlyList<DashboardScheduledTask> Tasks);

/// <summary>
/// The automatic-search pause gate (#1002), read from the last download reading.
/// <c>Enabled</c> false means the switch is off and nothing is ever held.
/// </summary>
public sealed record DashboardQueuePauseGate(
    bool Enabled,
    int Threshold,
    bool MoviesPaused,
    bool TvPaused,
    int MoviesWaiting,
    int TvWaiting,
    bool Unreadable,
    string? UnreadableReason);

public sealed record DashboardLibraryAutomation(
    string LibraryId,
    string Name,
    string MediaType,
    string Status,
    DateTimeOffset? NextSearchUtc,
    DateTimeOffset? NextMissingSearchUtc,
    DateTimeOffset? NextUpgradeSearchUtc,
    DateTimeOffset? LastCompletedUtc,
    string? LastError);

public sealed record DashboardSearchCycle(
    string LibraryId,
    string LibraryName,
    string MediaType,
    string Status,
    int Planned,
    int Queued,
    int Skipped,
    DateTimeOffset StartedUtc,
    DateTimeOffset? CompletedUtc);

public sealed record DashboardScheduledTask(
    string Key,
    string Name,
    string LastResult,
    DateTimeOffset? LastCompletedUtc,
    DateTimeOffset? NextRunUtc,
    string? LastError = null);

// --------------------------------------------------------------- indexers

public sealed record DashboardIndexersSection(
    DateTimeOffset AsOfUtc,
    int Total,
    int Healthy,
    int Failing,
    int RateLimited,
    int Disabled,
    int QueriesToday,
    int FailedQueriesToday,
    int GrabsToday,
    IReadOnlyList<DashboardFailingIndexer> TopFailing);

public sealed record DashboardFailingIndexer(string Id, string Name, string Reason);

// ------------------------------------------------------------- processors

public sealed record DashboardProcessorsSection(
    DateTimeOffset AsOfUtc,
    int Total,
    int Healthy,
    int Failing,
    int HandoffsToday,
    int Waiting);

// -------------------------------------------------------------- retention

public sealed record DashboardRetentionSection(
    DateTimeOffset AsOfUtc,
    int LeavingSoon,
    IReadOnlyList<DashboardLeavingTitle> NextLeaving,
    int ReadyToRemove,
    int RemovedLast7Days,
    long RemovedBytesLast7Days);

public sealed record DashboardLeavingTitle(string EntityId, string Title, string MediaType, DateTimeOffset RemoveAfterUtc);

// -------------------------------------------------------------- subtitles

/// <param name="Wanted">Files short of a language their library asked for.</param>
/// <param name="Held">Files in libraries that ask for subtitles which already have every language (files minus wanted; reads a little low when titles carry their own language override).</param>
public sealed record DashboardSubtitlesSection(DateTimeOffset AsOfUtc, int Wanted, int Held);

// --------------------------------------------------------------- requests

public sealed record DashboardRequestsSection(
    DateTimeOffset AsOfUtc,
    int SeerrServers,
    int SeerrHealthy,
    int SeerrFailing,
    IReadOnlyDictionary<string, int> AcquisitionsLast7Days,
    int ImportLists,
    IReadOnlyDictionary<string, int> ImportListsByLastSync,
    int ListTitlesAddedLast7Days);

// -------------------------------------------------------------- blocklist

public sealed record DashboardBlocklistSection(DateTimeOffset AsOfUtc, int Count);

// ----------------------------------------------------------------- health

public sealed record DashboardHealthSection(
    DateTimeOffset AsOfUtc,
    int Problems,
    int Unknown,
    int Suggestions,
    DateTimeOffset? LastRunUtc,
    IReadOnlyList<DashboardHealthProblem> TopProblems);

/// <param name="Link">The screen that clears it, as a web path.</param>
/// <param name="Snoozed">True while the person has snoozed it; a snoozed problem is not counted.</param>
public sealed record DashboardHealthProblem(
    string Id,
    string Title,
    string Category,
    string Message,
    DateTimeOffset? SinceUtc,
    string Link,
    bool Snoozed);

// ---------------------------------------------------------------- storage

/// <summary>Volumes come from a background disk reading refreshed about once a minute; a request never touches a disk or a share.</summary>
public sealed record DashboardStorageSection(
    DateTimeOffset AsOfUtc,
    DateTimeOffset? ReadingUtc,
    IReadOnlyList<DashboardVolume> Volumes);

/// <param name="Roles">What the volume is used for: data, library, downloads, processor.</param>
/// <param name="Paths">The configured folders on it.</param>
/// <param name="Available">False when the folder could not be read (offline share, missing folder).</param>
public sealed record DashboardVolume(
    string Id,
    string Label,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Paths,
    bool Available,
    long? TotalBytes,
    long? FreeBytes);

// ----------------------------------------------------------------- system

public sealed record DashboardSystemSection(
    DateTimeOffset AsOfUtc,
    string Version,
    bool UpdateAvailable,
    string? LatestVersion,
    long UptimeSeconds,
    DashboardMachine? Machine,
    DashboardJobCounts Jobs,
    IReadOnlyList<DashboardLane> Lanes,
    DateTimeOffset? LastBackupUtc,
    DateTimeOffset? NextBackupUtc,
    bool BackupsEnabled,
    bool InternetOutage,
    DateTimeOffset? OutageSinceUtc,
    int NotificationsUnread);

public sealed record DashboardMachine(
    DateTimeOffset CapturedUtc,
    double CpuPercent,
    long MemoryBytes,
    double? MemoryPercent);

public sealed record DashboardJobCounts(int Running, int Queued, int Failed);

/// <param name="State">running, idle, failing, restarting or disabled.</param>
public sealed record DashboardLane(string Name, string State, int ActiveJobs, string? LastError);

// --------------------------------------------------------------- activity

public sealed record DashboardActivitySection(
    DateTimeOffset AsOfUtc,
    int WindowHours,
    int SearchesRun,
    int DownloadsStarted,
    int ImportsCompleted,
    int CleanupRemovals,
    int RetentionRemovals,
    IReadOnlyList<DashboardActivityItem> Recent);

/// <summary>
/// One recent Activity row as stored: the raw <c>Category</c> code, its plain-words
/// <c>Label</c> from the shared activity mapping (#1086), and the message.
/// </summary>
public sealed record DashboardActivityItem(
    string Id,
    string Category,
    string Label,
    string Message,
    string? RelatedEntityType,
    string? RelatedEntityId,
    DateTimeOffset CreatedUtc);
