namespace Deluno.Contracts;

/// <summary>
/// The normalized reading of what a torrent's tracker is saying about it
/// (#878, cleanup sheet C5). Each client adapter maps its own protocol's
/// tracker fields onto this one shape: qBittorrent's <c>api/v2/torrents/trackers</c>,
/// Transmission's <c>trackerStats</c>, Deluge's <c>tracker_status</c>. Usenet has
/// no trackers, so its snapshots never carry anything but <see cref="Unknown"/>.
/// </summary>
public static class DownloadTrackerStatuses
{
    /// <summary>The client did not report anything usable, or the protocol has no trackers at all.</summary>
    public const string Unknown = "unknown";

    /// <summary>The tracker answered normally.</summary>
    public const string Ok = "ok";

    /// <summary>
    /// The tracker explicitly said this torrent is unregistered, removed, or
    /// otherwise something it will never serve again — distinct from the
    /// tracker being merely unreachable right now, which stays
    /// <see cref="Unknown"/> or <see cref="Ok"/> rather than this.
    /// </summary>
    public const string Unregistered = "unregistered";
}

/// <summary>
/// The small, normalized snapshot needed to evaluate download health. Keeping this
/// contract outside the integration adapters lets recovery remain independent of
/// any particular download-client protocol.
/// </summary>
public sealed record RecoveryQueueSnapshot(
    string Status,
    string? ErrorMessage,
    string? SourcePath,
    double SpeedMbps,
    DateTimeOffset AddedUtc,
    int EtaSeconds,
    string ReleaseName,
    int Peers = 0,
    /// <summary>
    /// The client itself said this is a local setup problem — a full disk, an
    /// I/O error, a permissions problem, or files missing from disk (#880,
    /// #881) — rather than anything about the release. Each client adapter
    /// sets this from what it actually reports (qBittorrent's own error
    /// states, Transmission's local-error code, Deluge's daemon message as
    /// distinct from a tracker error). A setup problem never counts a strike
    /// and never blocks or replaces the release: fixing the machine is what
    /// clears it, not a different download.
    /// </summary>
    bool IsSetupProblem = false,
    /// <summary>Percent complete (0-100), for a rule's completion-range scope (#905).</summary>
    double ProgressPercent = 0,
    /// <summary>Usenet has no peers or seeders, so peer/seed-based rules never apply to it.</summary>
    bool IsUsenetProtocol = false,
    /// <summary>Whether the tracker is private, as the client itself reports it (#815). Null when unknown; a rule scoped to public-only or private-only never matches an unknown privacy.</summary>
    bool? IsPrivate = null,
    /// <summary>One of <see cref="DownloadTrackerStatuses"/> (#878).</summary>
    string TrackerStatus = DownloadTrackerStatuses.Unknown,
    /// <summary>
    /// The files actually inside this download, as the client's own file
    /// listing reports them (#816) - qBittorrent's <c>api/v2/torrents/files</c>,
    /// Transmission's <c>files</c> field, Deluge's <c>get_torrent_status</c>
    /// files. Empty when the content blocklist is off, the client does not
    /// expose a file listing, or the listing has not been fetched yet; a
    /// content-blocklist match then falls back to the release name alone.
    /// </summary>
    IReadOnlyList<string>? Files = null,
    /// <summary>Total size of the download, in bytes, as the client reports it (#951, parity sheet G157's ignore-above-size). 0 when unknown - never matches an ignore-above-size ceiling, so an unread size never falsely exempts a download.</summary>
    long SizeBytes = 0,
    /// <summary>
    /// #951: whether the download client's own alternate/scheduled speed
    /// limit is turned on right now. Only qBittorrent's adapter reads this;
    /// every other client always reports false here, which the "too slow"
    /// rule's own skip-while-throttled option is documented to mean "has no
    /// effect on this client" rather than treating it as "not throttled".
    /// </summary>
    bool AlternateSpeedLimitOn = false,
    /// <summary>
    /// #951: a torrent added as a magnet link that has not yet resolved its
    /// own metadata - qBittorrent's own <c>metaDL</c> state. Only
    /// qBittorrent's adapter reports this; every other client always reports
    /// false here, so the magnet-metadata rule never trips for them.
    /// </summary>
    bool IsFetchingMetadata = false,
    /// <summary>
    /// #970: how much of this download's data is available across its peers
    /// right now (qBittorrent's own <c>availability</c>, 0-1, can exceed 1.0
    /// when more than one full copy is available). Null where the protocol
    /// has no such reading, which never trips the availability check below.
    /// </summary>
    double? Availability = null,
    /// <summary>
    /// #1207: whether this download's downloaded bytes grew since the previous check, read from the bytes Deluno
    /// stores per item between checks. False is "no progress", whatever speed the client reports (Deluge shows
    /// 0.000057 MB/s on a stuck torrent). Null when there is no earlier reading yet (first sighting), where the rule
    /// falls back to the reported speed.
    /// </summary>
    bool? BytesGrewSinceLastCheck = null);

public sealed record RecoveryHealthFinding(
    string Severity,
    string Kind,
    string Summary,
    string Evidence,
    string RecommendedAction,
    bool CanSafelyRetry,
    bool CanSafelyRemove,
    /// <summary>
    /// The specific configured rule (#905) that produced this finding, when
    /// one matched. Null for findings outside the rule engine (a client-
    /// reported error, a failed import, a suspicious file name) - those are
    /// still shown, but never strike or act on their own.
    /// </summary>
    string? RuleId = null,
    int? RuleStrikeLimit = null,
    int? RuleTimerMinutes = null,
    string? RuleActionPreset = null);

public interface IRecoveryHealthEvaluator
{
    IReadOnlyList<RecoveryHealthFinding> Evaluate(
        RecoveryQueueSnapshot item,
        DateTimeOffset capturedUtc,
        IReadOnlyList<DownloadHealthRule> rules,
        // Off by default (#816, #936); null/disabled means no content-blocklist finding is ever produced.
        ContentBlocklistSettings? contentBlocklist = null);
}
