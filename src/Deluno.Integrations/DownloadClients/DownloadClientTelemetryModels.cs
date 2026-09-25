using Deluno.Contracts;

namespace Deluno.Integrations.DownloadClients;

/// <summary>
/// The verbs a download client can be asked to perform.
///
/// <para>Named here rather than spelled at each call site, because two of them
/// differ by one word and mean very different things to a person: `delete`
/// leaves the files, `delete-with-data` takes them, and `forget` takes the
/// client's memory of ever having had the release.</para>
/// </summary>
public static class DownloadClientActions
{
    public const string Pause = "pause";
    public const string Resume = "resume";
    public const string Recheck = "recheck";

    /// <summary>Remove the transfer, leave what it downloaded.</summary>
    public const string Delete = "delete";

    /// <summary>Remove the transfer and the files it produced.</summary>
    public const string DeleteWithData = "delete-with-data";

    /// <summary>
    /// Remove the transfer, its files, and the record that stops the same
    /// release being accepted again.
    ///
    /// <para>Distinct from <see cref="DeleteWithData"/> on purpose, and the
    /// distinction is the whole point of a forced re-download. A torrent client
    /// refuses a release it still holds by infohash, so removing the transfer
    /// is enough. A usenet client refuses it from <em>history</em>, which
    /// survives the queue being emptied — so on SABnzbd and NZBGet, delete and
    /// forget are two different requests, and only one of them lets the release
    /// back in.</para>
    /// </summary>
    public const string Forget = "forget";
}

/// <summary>
/// Values shared between the two places a client says a download failed on
/// its own: a push webhook and a poll of its history (#647). One string, so
/// a dispatch already marked by one path is recognised by the other instead
/// of being recorded, blocked and searched for again.
/// </summary>
public static class DownloadClientFailureCodes
{
    /// <summary>
    /// The download client itself reported the failure — not a timeout Deluno
    /// inferred, and not an import Deluno tried and lost.
    /// </summary>
    public const string ClientReported = "client-reported-failure";
}

/// <summary>
/// Values a <see cref="DownloadClientHistoryItem.Outcome"/> can carry beyond
/// the shared <see cref="DownloadQueueStatuses"/> and the literal
/// <c>"failed"</c> every adapter already writes (#663).
/// </summary>
public static class DownloadClientHistoryOutcomes
{
    /// <summary>
    /// The client's own duplicate check correctly declined this release — it,
    /// or a better copy of it, is already held. Not a failure: nothing was
    /// lost and nothing needs blocklisting or searching again. NZBGet's
    /// history reports this as <c>DELETED/DUPE</c>, <c>DELETED/COPY</c> or
    /// <c>DELETED/GOOD</c>.
    /// </summary>
    public const string Duplicate = "duplicate";

    /// <summary>
    /// The download itself is not lost, but finishing it needs a person
    /// (#733). NZBGet's <c>WARNING/SPACE</c> means the release downloaded
    /// completely and unpacking stopped because the disk filled — freeing
    /// space and re-running post-processing in NZBGet recovers it, so it is
    /// not a failure: nothing is blocklisted and nothing is searched for
    /// again.
    /// </summary>
    public const string NeedsAttention = "needsAttention";
}

public static class DownloadQueueStatuses
{
    public const string Downloading = "downloading";
    public const string Queued = "queued";
    public const string ImportReady = "importReady";
    public const string Stalled = "stalled";
    public const string Processing = "processing";
    public const string Processed = "processed";
    public const string ProcessingFailed = "processingFailed";
    public const string WaitingForProcessor = "waitingForProcessor";
    public const string ImportQueued = "importQueued";
    public const string Imported = "imported";
    public const string ImportFailed = "importFailed";
    public const string Completed = "completed";

    /// <summary>
    /// The client is doing something to the payload that is not moving bytes on
    /// the wire — verifying a hash (qBittorrent's <c>checkingUP</c>/
    /// <c>checkingDL</c>/<c>checkingResumeData</c>) or relocating finished files
    /// (<c>moving</c>). #660: these used to fall through to
    /// <see cref="Downloading"/>, which let the no-progress and speed rules
    /// (#659) reason about throughput from a torrent that structurally cannot
    /// have any right now.
    /// </summary>
    public const string NotTransferring = "notTransferring";

    /// <summary>
    /// Stopped because a person stopped it — qBittorrent's <c>pausedDL</c>/
    /// <c>stoppedDL</c>, Transmission's stopped status, Deluge's <c>Paused</c>
    /// state — distinct from <see cref="Queued"/>, which is the client holding
    /// an item back on its own behind others (#660/#661). Collapsing the two
    /// told a person a download was merely waiting its turn when they had, in
    /// fact, stopped it themselves.
    /// </summary>
    public const string Paused = "paused";
}

internal static class DownloadClientTelemetryLimits
{
    // The history drawer is a recent-activity view, not an archive. Dispatch
    // history in jobs.db is the archive, so one explicit window protects every
    // client adapter without silently varying by protocol.
    public const int HistoryWindow = 200;
}

public sealed record DownloadTelemetrySummary(
    int ActiveCount,
    int QueuedCount,
    int CompletedCount,
    int StalledCount,
    /// <summary>
    /// Everything past the download and before the library: items handed to a
    /// post-processor and items whose import job is queued or running. The two
    /// are different problems when they stick, so
    /// <see cref="WaitingForProcessorCount"/> reports the processor share
    /// separately rather than leaving callers to label the whole bucket.
    /// </summary>
    int ProcessingCount,
    int ImportReadyCount,
    double TotalSpeedMbps,
    /// <summary>
    /// Downloads finished but held back because their library refines before
    /// importing and the cleaned output has not arrived. A subset of
    /// <see cref="ProcessingCount"/>; the remainder is import work.
    /// </summary>
    int WaitingForProcessorCount = 0,
    /// <summary>
    /// Combined upload across every client, in MB/s. Deluno holds files back so
    /// a site's sharing rule can be met (#288), which makes "am I actually
    /// seeding?" a question it has to be able to answer (#289).
    /// </summary>
    double TotalUploadSpeedMbps = 0);

public sealed record DownloadQueueItem(
    string Id,
    string ClientId,
    string ClientName,
    string Protocol,
    string MediaType,
    string Title,
    string ReleaseName,
    string Category,
    string Status,
    double Progress,
    double SpeedMbps,
    int EtaSeconds,
    long SizeBytes,
    long DownloadedBytes,
    int Peers,
    string IndexerName,
    string? ErrorMessage,
    DateTimeOffset AddedUtc,
    string? SourcePath = null,
    string? LibraryId = null,
    IReadOnlyList<DownloadHealthFinding>? HealthFindings = null,
    /// <summary>
    /// Upload divided by download, as the client reports it. Null when the
    /// client does not track it — usenet has no such notion, and a rule that
    /// asks for a ratio can never be satisfied there.
    /// </summary>
    double? Ratio = null,
    /// <summary>
    /// How long the client has been sharing this item since it completed. Null
    /// where the protocol has no sharing phase.
    /// </summary>
    int? SeedingMinutes = null,
    /// <summary>
    /// What this item is currently giving back, in MB/s. Always zero on usenet,
    /// which has nothing to upload.
    /// </summary>
    double UploadSpeedMbps = 0,
    IntegrationFailure? Failure = null);

/// <summary>
/// An observational queue-health signal. These findings never cause Deluno to remove
/// download data; they explain why an item needs attention and which reversible action
/// a person can consider first.
/// </summary>
public sealed record DownloadHealthFinding(
    string Severity,
    string Kind,
    string Summary,
    string Evidence,
    string RecommendedAction,
    bool CanSafelyRetry,
    bool CanSafelyRemove,
    int StrikeCount = 0,
    bool CandidateBlocked = false,
    DateTimeOffset? IgnoredUntilUtc = null);

public sealed record DownloadClientHistoryItem(
    string Id,
    string ClientId,
    string ClientName,
    string Protocol,
    string MediaType,
    string Title,
    string ReleaseName,
    string Category,
    string Outcome,
    string IndexerName,
    long SizeBytes,
    DateTimeOffset CompletedUtc,
    string? ErrorMessage,
    string? SourcePath = null,
    string HistorySource = "queue-derived",
    string? ExternalId = null,
    IntegrationFailure? Failure = null);

public sealed record DownloadClientTelemetryCapabilities(
    bool SupportsQueue,
    bool SupportsHistory,
    bool SupportsPauseResume,
    bool SupportsRemove,
    bool SupportsRecheck,
    bool SupportsImportPath,
    string AuthMode);

public sealed record DownloadClientTelemetrySnapshot(
    string ClientId,
    string ClientName,
    string Protocol,
    string? EndpointUrl,
    string HealthStatus,
    string? LastHealthMessage,
    DownloadClientTelemetryCapabilities Capabilities,
    DownloadTelemetrySummary Summary,
    IReadOnlyList<DownloadQueueItem> Queue,
    IReadOnlyList<DownloadClientHistoryItem> History,
    DateTimeOffset CapturedUtc,
    bool HistoryTruncated = false)
{
    /// <summary>Last live integration failure, if this snapshot is degraded.</summary>
    public IntegrationFailure? LastFailure { get; init; }
}

public sealed record DownloadTelemetryOverview(
    DownloadTelemetrySummary Summary,
    IReadOnlyList<DownloadClientTelemetrySnapshot> Clients,
    DateTimeOffset CapturedUtc);

public sealed record DownloadClientActionRequest(
    string Action,
    string QueueItemId);

public sealed record DownloadClientActionResult(
    string ClientId,
    string QueueItemId,
    string Action,
    bool Succeeded,
    string Message)
{
    /// <summary>Attributable failure details when the client rejected the action.</summary>
    public IntegrationFailure? Failure { get; init; }
}

public sealed record DownloadHealthRemediationReport(
    int Evaluated,
    int ReplacementSearchesQueued,
    int ClientEntriesRemoved,
    int PayloadsPurged,
    int Skipped,
    IReadOnlyList<string> Notes,
    int ReleasesRefused = 0);
