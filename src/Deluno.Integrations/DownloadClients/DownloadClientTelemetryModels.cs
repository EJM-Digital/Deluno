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
    /// Remove a copy Deluno has decided against (a refused download, or the old copy a forced re-download is
    /// replacing): the transfer, its files, and the record that stops the same release being accepted again.
    ///
    /// <para>This is the only verb that removes a download as a clean-up, and it has its own name on purpose.
    /// It used to be called "forget" and was also what "Just stop tracking it" sent, so a person who asked
    /// Deluno only to stop tracking a download lost it and its files from the client (#1241). Stopping
    /// tracking is Deluno-only and sends the client nothing, so it has no verb at all.</para>
    ///
    /// <para>Distinct from <see cref="DeleteWithData"/>, and the distinction is the whole point of a forced
    /// re-download. A torrent client refuses a release it still holds by infohash, so removing the transfer
    /// is enough. A usenet client refuses it from <em>history</em>, which survives the queue being emptied,
    /// so on SABnzbd and NZBGet, delete and this are two different requests, and only this one lets the
    /// release back in.</para>
    /// </summary>
    public const string RemoveRefusedCopy = "remove-refused-copy";

    // #810: queue priority/reorder. Each adapter that supports queueing maps
    // these onto its own native call (increasePrio/topPriority for
    // qBittorrent, queue-move-* for Transmission, queue_top/up/down/bottom for
    // Deluge, the priority parameter for SABnzbd, GroupMoveTop/GroupMoveBottom/
    // GroupSetPriority for NZBGet). uTorrent is left without these: its queue
    // API could not be independently confirmed this session (closed-source,
    // no public source repo).
    public const string MoveToTop = "move-to-top";
    public const string MoveUp = "move-up";
    public const string MoveDown = "move-down";
    public const string MoveToBottom = "move-to-bottom";

    /// <summary>qBittorrent only: bypass its own seed/ratio thresholds and force the transfer to start now.</summary>
    public const string ForceStart = "force-start";

    private static readonly HashSet<string> QueuePriorityOrForceStartActions = new(StringComparer.Ordinal)
    {
        MoveToTop, MoveUp, MoveDown, MoveToBottom, ForceStart
    };

    /// <summary>#810/#933: whether this action is one of the queue-reorder/force-start verbs, which are only ever allowed against a download Deluno itself dispatched.</summary>
    public static bool IsQueuePriorityOrForceStart(string action) => QueuePriorityOrForceStartActions.Contains(action);

    /// <summary>
    /// #1062/#933: every verb on a queue item - pause, resume, recheck,
    /// reorder, force-start, remove - acts only on a download Deluno sent.
    /// Deluno does not act on anything else in a client.
    /// </summary>
    public static bool RequiresDelunoOwnership(string action) => true;
}

/// <summary>
/// The three plain-word choices behind the Queue's "Remove" dialog
/// (cleanup sheet J1/J2, #924). Replaces the old single yes/no confirm and
/// the "Allow removing client queue entries" setting that gated it — the
/// dialog is now the confirmation, and every choice acts.
/// </summary>
public static class QueueRemovalReasons
{
    /// <summary>
    /// "It's bad": remove from the client, delete its files, blocklist the
    /// release everywhere it could be found again, and search for another.
    /// </summary>
    public const string Failed = "failed";

    /// <summary>
    /// "I don't want it": remove from the client and delete its files. No
    /// blocklist — the release was not bad, the title just is not wanted.
    /// </summary>
    public const string Unwanted = "unwanted";

    /// <summary>
    /// "Just stop tracking it": Deluno forgets the download. The client keeps
    /// it and its files, untouched: nothing at all is sent to the client (#1241).
    /// </summary>
    public const string Forget = "forget";

    /// <summary>
    /// "Remove this copy": a finished download whose files no longer share data with the library. Remove it from
    /// the client with its files and judge nothing: no blocklist, no search, the title stays wanted as it was.
    /// </summary>
    public const string Reclaim = "reclaim";

    /// <summary>
    /// "Search for another release" on a hand-off: the release was given up on (and blocklisted by the caller), so
    /// the download leaves the client with its files. The title stays wanted; the caller starts the search.
    /// </summary>
    public const string GaveUp = "gave-up";

    public static bool IsKnown(string reason)
        => reason is Failed or Unwanted or Forget or Reclaim or GaveUp;
}

public sealed record QueueRemovalRequest(string Reason);

/// <summary>
/// What one removal does, part by part. The three <see cref="QueueRemovalReasons"/>
/// are fixed combinations of these; the API's removeFromClient/blocklist flags
/// (#1022) pick their own.
/// </summary>
/// <param name="RemoveFromClient">Delete it from the client with its files; otherwise Deluno only forgets it.</param>
/// <param name="Blocklist">Refuse the release everywhere it could be found again.</param>
/// <param name="Search">Look for a replacement (only with a blocklist).</param>
/// <param name="Unmonitor">Stop wanting the title (the "I don't want it" choice).</param>
public sealed record QueueRemovalOptions(bool RemoveFromClient, bool Blocklist, bool Search, bool Unmonitor);

/// <param name="EntityId">
/// The title this download belongs to, when Deluno can still say — read from
/// the matching dispatch, not the queue item itself. Null for a download
/// Deluno never grabbed (added by hand, or from before this dispatch existed),
/// in which case only the client action happens.
/// </param>
/// <param name="MediaType">"movie" or "tv", alongside <see cref="EntityId"/>.</param>
public sealed record QueueRemovalResult(
    string ClientId,
    string QueueItemId,
    string Reason,
    bool Succeeded,
    string Message,
    bool Blocklisted,
    bool SearchStarted,
    string? EntityId,
    string? MediaType)
{
    public IntegrationFailure? Failure { get; init; }

    /// <summary>True when Deluno refused because it did not send this download (#1062, #933); the API answers 409.</summary>
    public bool Refused { get; init; }
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

    /// <summary>
    /// Deluno opened the finished file and it was not what its name claimed
    /// (#1148). Recorded on the dispatch so Transfers shows the same failure
    /// Activity reports instead of "Waiting for processor".
    /// </summary>
    public const string IntegrityCheckFailed = "download-integrity-failed";
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

    /// <summary>
    /// The download itself finished, but the client's own post-processing
    /// script failed (#910) — NZBGet's <c>FAILURE/SCRIPT</c>. A setup problem
    /// with the client's script, not a bad release: never blocklisted, never
    /// searched for again. The downloaded file is still handed to the
    /// ordinary import path, which probes it and either imports it or follows
    /// the bad-release path if the file itself turns out to be broken.
    /// </summary>
    public const string ScriptFailed = "scriptFailed";

    /// <summary>
    /// The same as <see cref="ScriptFailed"/>, but the client only warned
    /// (#910) — NZBGet's <c>WARNING/SCRIPT</c>. Shown, not silently treated as
    /// a plain success.
    /// </summary>
    public const string ScriptWarning = "scriptWarning";
}

public static class DownloadQueueStatuses
{
    /// <summary>
    /// A grab result's status when the client itself said, in the same
    /// request, that the release it was handed is unusable (#876) — a dead
    /// link or a file it could not parse. The caller blocklists the release
    /// and lets the next search pick a different one, instead of waiting for
    /// an ordinary retry cycle to fail the same way again.
    /// </summary>
    public const string DeadRelease = "deadRelease";
    public const string Downloading = "downloading";
    public const string Queued = "queued";
    public const string ImportReady = "importReady";
    public const string Stalled = "stalled";
    public const string Processing = "processing";
    public const string Processed = "processed";
    public const string ProcessingFailed = "processingFailed";
    public const string WaitingForProcessor = "waitingForProcessor";

    /// <summary>
    /// A finished download another copy of the same release has the hand-off for (#1155): nothing is
    /// owed to this one, so it is never "handing over" for ever.
    /// </summary>
    public const string HandedOverElsewhere = "handedOverElsewhere";

    /// <summary>
    /// A finished download that still has no hand-off of its own after a few minutes (#1155): something
    /// is stopping Deluno handing it over, and the row says what.
    /// </summary>
    public const string NotHandedOver = "notHandedOver";
    public const string ImportQueued = "importQueued";
    public const string Imported = "imported";

    /// <summary>
    /// Imported earlier, but the title no longer holds the file (a wrong episode moved to the recycle bin, say) and
    /// nothing will import this download again (#1182). History, never an active import: not "In library".
    /// </summary>
    public const string ImportedFileLeft = "importedFileLeft";

    /// <summary>
    /// The download's files are gone: the client still lists it as done but its files are not on disk (#1200), or
    /// its hand-off ended because they were gone and was dismissed. History or a problem to clear, never ready to import.
    /// </summary>
    public const string FilesGone = "filesGone";
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
    double TotalUploadSpeedMbps = 0,
    /// <summary>
    /// Finished torrents the client is still sharing (#1098): protocols with a sharing phase, fully
    /// downloaded and not paused. Items Deluno did not send are not in a queue, so are not counted.
    /// Always zero for Usenet.
    /// </summary>
    int SeedingCount = 0);

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
    IntegrationFailure? Failure = null,
    /// <summary>
    /// Set by the client adapter when the client itself reports a local setup
    /// problem — a full disk, an I/O error, a permissions problem, or files
    /// missing from disk — rather than anything about the release (#880,
    /// #881). Carried through to <see cref="Deluno.Contracts.RecoveryQueueSnapshot"/>
    /// so it never earns a strike or triggers the cleanup remediation path.
    /// </summary>
    bool IsSetupProblem = false,
    /// <summary>
    /// Whether the torrent's own tracker is private, as the client itself
    /// reports it (#815) - qBittorrent, Transmission and Deluge each expose
    /// this. Null when the client did not say (an older client version,
    /// Usenet, which has no tracker at all) - a rule scoped to public-only
    /// or private-only never matches an unknown privacy, so an unreadable
    /// flag never wrongly narrows a private-tracker download's protection.
    /// </summary>
    bool? IsPrivate = null,
    /// <summary>
    /// One of <see cref="Deluno.Contracts.DownloadTrackerStatuses"/> (#878):
    /// each torrent client adapter reads its own protocol's tracker fields
    /// (qBittorrent's <c>api/v2/torrents/trackers</c>, Transmission's
    /// <c>trackerStats</c>, Deluge's <c>tracker_status</c>) and normalizes them
    /// here. Usenet clients never set this to anything but Unknown.
    /// </summary>
    string TrackerStatus = Deluno.Contracts.DownloadTrackerStatuses.Unknown,
    /// <summary>
    /// #811: how much of this torrent's data is available across its peers
    /// right now (qBittorrent's own <c>availability</c>, 0-1, can exceed 1.0
    /// when more than one full copy is available). Null where the protocol
    /// has no such reading.
    /// </summary>
    double? Availability = null,
    /// <summary>#811: when the client last saw any activity on this item (qBittorrent's <c>last_activity</c>). Null where the protocol has no such reading.</summary>
    DateTimeOffset? LastActivityUtc = null,
    /// <summary>#811: when the client finished this item (qBittorrent's <c>completion_on</c>). Null while still in progress, or where the protocol has no such reading.</summary>
    DateTimeOffset? CompletionUtc = null,
    /// <summary>
    /// #951: a torrent added as a magnet link that has not yet resolved its
    /// own metadata (name, file list, size) - qBittorrent's own
    /// <c>metaDL</c> state. Only qBittorrent's adapter sets this; every
    /// other client leaves it false, since nothing else in what Deluno reads
    /// from them distinguishes this from ordinary downloading.
    /// </summary>
    bool IsFetchingMetadata = false,
    /// <summary>
    /// #1026: this item's place in the client's own queue, 1 being next to
    /// start (Transmission's <c>queuePosition</c>, Deluge's <c>queue</c>, both
    /// counted from 0 by the client). Null where the client reports none
    /// (Deluge says -1 for a torrent that is not queued) or does not expose it.
    /// </summary>
    int? QueuePosition = null,
    /// <summary>
    /// #1026: this item's own seed limits in plain words, when the client
    /// holds ones specific to the torrent ("Stops seeding at ratio 2 or after
    /// 30 minutes with no uploading"), or a plain statement that it follows
    /// the client's overall limits or never stops. Null where the client says
    /// nothing about it.
    /// </summary>
    string? SeedLimit = null,
    /// <summary>
    /// An id this client used to file the item under (Transmission's numeric
    /// torrent id, before ownership moved to its hash). Never used to claim an
    /// item; only to recognise a dispatch recorded under the old id and move it
    /// to the current one, when the client shows the item under that dispatch's
    /// own release name (#1062).
    /// </summary>
    [property: System.Text.Json.Serialization.JsonIgnore]
    string? LegacyId = null,
    /// <summary>
    /// The client's own tags or labels on this item (#1194), for the ignore list. Never sent to a screen.
    /// </summary>
    [property: System.Text.Json.Serialization.JsonIgnore]
    IReadOnlyList<string>? Tags = null,
    /// <summary>
    /// The hosts of this torrent's tracker announce URLs (#1194), for the ignore list's tracker-domain entries.
    /// Never sent to a screen.
    /// </summary>
    [property: System.Text.Json.Serialization.JsonIgnore]
    IReadOnlyList<string>? TrackerHosts = null,
    /// <summary>
    /// Whether the downloaded bytes grew since the previous check (#1207), from the bytes stored per item between
    /// checks. Null when there is no earlier reading. Feeds the No progress rule; never sent to a screen.
    /// </summary>
    [property: System.Text.Json.Serialization.JsonIgnore]
    bool? BytesGrewSinceLastCheck = null);

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
    DateTimeOffset? IgnoredUntilUtc = null,
    /// <summary>The configured rule (#905) driving this finding's strikes, when one matched.</summary>
    string? RuleId = null,
    string? RuleActionPreset = null,
    DateTimeOffset? NextStrikeUtc = null,
    /// <summary>The matched rule's own strike limit (#905); null for a finding outside the rule engine, which uses the platform-wide fallback threshold instead.</summary>
    int? RuleStrikeLimit = null,
    /// <summary>When this spell of the problem began (the first sighting with no break since), so a message can say how long it has really lasted.</summary>
    DateTimeOffset? SpellStartedUtc = null);

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
    string AuthMode,
    /// <summary>#810: this client's own queue can be reordered (move to top/up/down/bottom). False for a protocol whose queue API could not be confirmed (uTorrent).</summary>
    bool SupportsQueueReorder = false,
    /// <summary>#810: qBittorrent only -- bypass its own seed/ratio thresholds and force a transfer to start now.</summary>
    bool SupportsForceStart = false);

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

    /// <summary>
    /// The paths of items in this client that Deluno did not send (#1062). Only
    /// the leftover-download scan asks for them, and only so a file another
    /// tool is still using is never swept up; nothing lists or counts them, and
    /// they never reach the API.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> OutsideSourcePaths { get; init; } = [];

    /// <summary>
    /// Free space on the client's own download volume, exactly as the client's
    /// API reports it (#909) -- null when the protocol has no such call
    /// (uTorrent's WebUI) rather than a guess dressed up as a reading.
    /// </summary>
    public long? FreeSpaceBytes { get; init; }

    /// <summary>
    /// The client itself reports it is paused or on standby (#812) -- SABnzbd's
    /// global pause flag, or NZBGet's ServerStandBy/DownloadPaused/PostPaused/
    /// ScanPaused. Distinct from Deluno being unable to reach the client: this
    /// is the client's own choice, not a connection problem, and null when the
    /// protocol has no such reading.
    /// </summary>
    public bool? ClientPaused { get; init; }

    /// <summary>The client's own reported version (#812), read alongside its queue/status. Null when not yet read or the protocol has no version call.</summary>
    public string? ClientVersion { get; init; }

    /// <summary>
    /// The client's own queue-level warnings (#813) -- SABnzbd's
    /// have_warnings/mode=warnings, such as a disk-full condition or a failed
    /// unpack. Null when the client has none, or the protocol has no such
    /// concept.
    /// </summary>
    public IReadOnlyList<string>? ClientWarnings { get; init; }

    /// <summary>
    /// #951: whether the client's own alternate/scheduled speed limit is
    /// turned on right now, for the "too slow" rule's skip-while-throttled
    /// option. Only qBittorrent's adapter reads this (its
    /// <c>server_state.use_alt_speed_limits</c>); null everywhere else,
    /// which the evaluator reads the same as "off" - never a false trip on a
    /// client that never claimed to be throttled.
    /// </summary>
    public bool? AlternateSpeedLimitEnabled { get; init; }
}

public sealed record DownloadTelemetryOverview(
    DownloadTelemetrySummary Summary,
    IReadOnlyList<DownloadClientTelemetrySnapshot> Clients,
    DateTimeOffset CapturedUtc);

/// <summary>
/// The body of the public queue-actions route. It carries only what the caller
/// may say - the action and the item - and never a claim about who owns the
/// item: ownership is always decided server-side from Deluno's own dispatch
/// records (#1062). Internal callers that hold a dispatch use
/// <see cref="IDownloadClientTelemetryService.ExecuteActionForDispatchAsync"/>.
/// </summary>
public sealed record DownloadClientActionRequest(
    string Action,
    string QueueItemId);

/// <summary>What asking a whole download client to resume came to (a client-level pause, not one item).</summary>
public sealed record ClientResumeResult(bool Supported, bool Succeeded, string Message)
{
    public static ClientResumeResult NotSupported { get; } = new(false, false, "This download client has no pause of its own to resume.");
}

public sealed record DownloadClientActionResult(
    string ClientId,
    string QueueItemId,
    string Action,
    bool Succeeded,
    string Message)
{
    /// <summary>Attributable failure details when the client rejected the action.</summary>
    public IntegrationFailure? Failure { get; init; }

    /// <summary>True when Deluno refused because it did not send this download (#1062, #933); the API answers 409.</summary>
    public bool Refused { get; init; }

    /// <summary>
    /// Set when the download was removed from the client but its files are still on disk, and why (#1212). A removal
    /// that carries this must never be reported as having deleted the files.
    /// </summary>
    public string? FilesNote { get; init; }
}

/// <summary>Whether Deluno itself sent a download; <see cref="Message"/> says so in plain words when it did not.</summary>
public sealed record DownloadOwnership(bool Owned, string? Message);

public sealed record DownloadHealthRemediationReport(
    int Evaluated,
    int ReplacementSearchesQueued,
    int ClientEntriesRemoved,
    int PayloadsPurged,
    int Skipped,
    IReadOnlyList<string> Notes,
    int ReleasesRefused = 0);

/// <summary>
/// What one real pass over the download clients found: the overview every screen
/// shows and, from the same pass, the leftover-scan view that also knows the
/// paths of items Deluno did not send (#1081).
/// </summary>
public sealed record DownloadTelemetryCapture(
    DownloadTelemetryOverview Overview,
    IReadOnlyList<DownloadClientTelemetrySnapshot> LeftoverSnapshots);
