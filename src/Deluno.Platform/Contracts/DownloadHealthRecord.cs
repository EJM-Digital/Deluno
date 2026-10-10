using Deluno.Contracts;

namespace Deluno.Platform.Contracts;

/// <summary>
/// Durable, observational evidence for a problematic download. It is deliberately
/// separate from cleanup actions: a blocked candidate cannot cause file removal.
/// </summary>
public sealed record DownloadHealthObservation(
    string ClientId,
    string QueueItemId,
    string ReleaseName,
    string Kind,
    string Severity,
    string Evidence,
    /// <summary>
    /// The specific configured rule (#905) this observation came from, and the
    /// strike/timer/action it carries at the moment it was observed. Null for
    /// findings outside the rule engine, which fall back to the platform's
    /// global strike settings.
    /// </summary>
    string? RuleId = null,
    int? RuleStrikeLimit = null,
    int? RuleTimerMinutes = null,
    string? RuleActionPreset = null);

/// <summary>
/// One problem with one download, for as long as it lasts.
/// </summary>
/// <param name="FirstObservedUtc">
/// When this spell of the problem began: the first sighting with no break
/// since. A download that recovers and then stalls again starts a new spell.
/// </param>
/// <param name="StrikeCount">
/// How long the spell has lasted, in strikes. See <see cref="DownloadHealthStrikes"/>.
/// </param>
public sealed record DownloadHealthRecord(
    string ClientId,
    string QueueItemId,
    string ReleaseName,
    string ReleaseKey,
    string Kind,
    string Severity,
    string Evidence,
    DateTimeOffset FirstObservedUtc,
    DateTimeOffset LastObservedUtc,
    int StrikeCount,
    DateTimeOffset? IgnoredUntilUtc,
    string? RuleId = null,
    int? RuleStrikeLimit = null,
    int? RuleTimerMinutes = null,
    string? RuleActionPreset = null,
    DateTimeOffset? LastStrikeUtc = null)
{
    public bool IsIgnored(DateTimeOffset now) => IgnoredUntilUtc is { } ignoredUntil && ignoredUntil > now;

    /// <summary>
    /// <paramref name="fallbackThreshold"/> is the platform-wide strike
    /// threshold, used only for findings outside the rule engine (#905) -
    /// every rule-engine finding carries its own <see cref="RuleStrikeLimit"/>.
    /// </summary>
    public bool BlocksCandidate(DateTimeOffset now, int fallbackThreshold = 3)
        => DownloadHealthStrikes.Counts(Kind) &&
           StrikeCount >= Math.Clamp(RuleStrikeLimit ?? fallbackThreshold, 1, 100) &&
           !IsIgnored(now);

    /// <summary>When the next check can add a strike: one check interval after the last strike (#1194).</summary>
    public DateTimeOffset NextStrikeUtc(int checkIntervalMinutes = 5)
        => (LastStrikeUtc ?? FirstObservedUtc) + TimeSpan.FromMinutes(Math.Clamp(checkIntervalMinutes, 1, 1440));
}

/// <summary>
/// Durable, restart-safe memory of one queue item's downloaded-bytes
/// progress (#659). Kept separately from <see cref="DownloadHealthRecord"/>
/// because it is not a finding: it is watched for every item the client
/// lists, not only one already in trouble, and it never itself counts
/// toward a strike. A stall check reads <see cref="LastIncreaseUtc"/> to ask
/// "how long has this gone without gaining a byte", a question that has to
/// survive a process restart or every restart looks like fresh progress.
/// </summary>
public sealed record DownloadProgressRecord(
    string ClientId,
    string QueueItemId,
    long DownloadedBytes,
    DateTimeOffset LastIncreaseUtc,
    DateTimeOffset LastObservedUtc);

/// <summary>What one check saw of an item's downloaded bytes (#1207).</summary>
/// <param name="LastIncreaseUtc">When the bytes were last seen to grow, as the progress clock counts it.</param>
/// <param name="BytesGrew">Whether the bytes grew since the previous check; null when there was no earlier reading.</param>
public sealed record DownloadProgressObservation(DateTimeOffset LastIncreaseUtc, bool? BytesGrew);

/// <summary>
/// What a strike is (#560).
///
/// <para><b>A strike is time, not a sighting.</b> One strike when a problem is
/// first seen, and one more for every timer window it is still there - the
/// rule engine (#905) gives each rule its own timer; findings outside the
/// rule engine use <see cref="DefaultWindow"/>. The first version counted
/// sightings more than half an hour apart. So a download that stayed stalled
/// never passed one strike, while a torrent that was "stalled" for a few seconds
/// at the start of three healthy downloads, days apart, had its release blocked
/// for 90 days.</para>
///
/// <para><b>Only a download that has stopped counts.</b> The strike policy
/// refuses the release and can search for another, which answers one question:
/// is this download going to arrive? A failed import or a failed processing run
/// has its own rules (DESIGN-007), which decide whether that release is refused.
/// A long estimate is a download that is still moving. Those findings are still
/// shown. They are not strikes.</para>
/// </summary>
public static class DownloadHealthStrikes
{
    /// <summary>
    /// How long a problem must last for each further strike, for a finding
    /// outside the rule engine (#823 makes this a setting rather than a
    /// constant; every rule-engine finding uses its own rule's timer instead).
    /// </summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(30);

    /// <summary>The client says it is stalled, or reports an error.</summary>
    public const string ClientStalled = "client-stalled";

    /// <summary>
    /// The client itself reports a local setup problem — a full disk, an I/O
    /// error, a permissions problem, or files missing from disk (#880, #881)
    /// — rather than anything about the release. Deliberately excluded from
    /// <see cref="Counts"/>: a strike, a blocklist entry and a replacement
    /// search all answer "is this release dead", and a setup problem does not
    /// touch that question. Fixing the machine clears it; nothing here should
    /// ever remove the download, delete its files, or search for another.
    /// </summary>
    public const string ClientSetupProblem = "client-setup-problem";

    /// <summary>
    /// The client's own post-processing script failed or warned after the
    /// download itself finished (#910) — NZBGet's FAILURE/SCRIPT and
    /// WARNING/SCRIPT. A setup problem with the client's script, not the
    /// release: deliberately excluded from <see cref="Counts"/> the same way
    /// <see cref="ClientSetupProblem"/> is, so it never blocklists, removes or
    /// searches for a replacement on its own. The downloaded file is still
    /// probed and imported when it is complete and playable.
    /// </summary>
    public const string ClientScriptFailed = "client-script-failed";

    /// <summary>Nothing has arrived for at least the rule's timer.</summary>
    public const string NoThroughput = "no-progress";

    /// <summary>Slower than the configured floor for longer than allowed (#659, off by default).</summary>
    public const string LowSpeed = "too-slow";

    /// <summary>Fewer peers or seeders than the configured floor for longer than allowed (#659, off by default).</summary>
    public const string LowPeers = "low-peers";

    /// <summary>Zero peers or seeders, or the tracker itself saying unregistered, past the rule's grace period (#818, off by default).</summary>
    public const string DeadTorrent = "dead-torrent";

    /// <summary>
    /// The tracker explicitly reports this torrent unregistered or removed
    /// (#878, cleanup sheet C5). Not gated behind a configurable rule the way
    /// <see cref="DeadTorrent"/> is: James decided this acts immediately once
    /// the tracker has said so, rather than waiting for strikes to build up,
    /// so this kind always carries its own strike limit of one.
    /// </summary>
    public const string TrackerUnregistered = "tracker-unregistered";

    /// <summary>
    /// A file inside the download matched the content blocklist, and the
    /// dedicated rule (#879, off by default) is enabled - a single instant
    /// strike, not a timed one: one confirmed dangerous file is enough.
    /// </summary>
    public const string DangerousFile = "dangerous-file";

    /// <summary>
    /// The same client-reported failure as <see cref="ClientStalled"/>, but
    /// struck through the dedicated, configurable rule (#951, off by
    /// default) instead of the platform-wide fallback threshold.
    /// </summary>
    public const string ClientFailure = "client-failure";

    /// <summary>A magnet stuck fetching its own metadata, past the dedicated rule's timer (#951, off by default). qBittorrent only.</summary>
    public const string MagnetMetadata = "magnet-metadata";

    /// <summary>
    /// The client's own availability reading is below 1 (#811, #970): no
    /// single peer holds a complete copy of this release right now, so it
    /// may never finish no matter how many peers are connected. A read on
    /// the swarm's composition, not this machine's behaviour — excluded from
    /// <see cref="Counts"/> the same way <see cref="ClientScriptFailed"/> is,
    /// so it is shown and can inform a replacement search without acting on
    /// its own.
    /// </summary>
    public const string LowAvailability = "low-availability";

    /// <summary>
    /// Whether a download is finished, or already in (or past) Deluno's own
    /// processing, so that nothing about its transfer can be "stalled" (#1154).
    /// A complete download that waits on a processor hand-off, or sits after a
    /// failed one for the person to decide, is not a dead release. Only the
    /// library's "if processing fails" choice, or the person, may act on it.
    /// </summary>
    public static bool IsFinishedOrHandedOff(string? status, double progressPercent)
        => IsFinishedPercent(progressPercent) ||
           status is "completed" or "importReady" or "processing" or "processed" or "processingFailed"
               or "waitingForProcessor" or "notHandedOver" or "handedOverElsewhere" or "importQueued" or "imported" or "importFailed";

    /// <summary>
    /// From here an unfinished download reads "Finishing" on screen (#1193). It is a label only: the stall and
    /// slow-speed strike rules apply at every percent below 100, because a torrent stuck at 99.9x% for want of one
    /// piece is a real stall.
    /// </summary>
    public const double FinishingPercent = 99.9d;

    /// <summary>
    /// The one finished-by-percent rule every client adapter uses (#1198): only 100% is finished. There is no 99.9%
    /// threshold anywhere, because a torrent stuck at 99.9x% is a real stall. A client's own state (completed,
    /// seeding, upload) may also say a download is complete; that is the adapter's own mapping.
    /// </summary>
    public static bool IsFinishedPercent(double? progressPercent) => (progressPercent ?? 0d) >= 100d;

    /// <summary>
    /// The one kind of finding that may still act on a finished download: a
    /// dangerous or junk file inside it. Every stall, error, speed, peer and
    /// tracker rule is about getting the bytes, which a finished download has.
    /// </summary>
    public static bool MayActOnFinishedDownload(string kind)
        => string.Equals(kind, DangerousFile, StringComparison.OrdinalIgnoreCase);

    public static bool Counts(string kind)
        => string.Equals(kind, ClientStalled, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(kind, NoThroughput, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(kind, LowSpeed, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(kind, LowPeers, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(kind, DeadTorrent, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(kind, TrackerUnregistered, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(kind, DangerousFile, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(kind, ClientFailure, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(kind, MagnetMetadata, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A check interval of five minutes is the default (#1194): each check where a problem holds adds one strike, so
    /// removal lands (strike limit - 1) intervals after the first strike.
    /// </summary>
    public static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether a check at <paramref name="now"/> adds a strike (#1194). The observation pass runs more often than the
    /// check interval, so a pass less than one interval after the last strike adds nothing. There is no allowance: a
    /// strike lands once per interval, never earlier (#1198).
    /// </summary>
    public static bool IsDueForStrike(DateTimeOffset lastStrikeUtc, DateTimeOffset now, TimeSpan checkInterval, TimeSpan outageOverlap)
        => now - lastStrikeUtc - outageOverlap >= checkInterval;

    /// <summary>
    /// When a strike that is due is recorded as having landed (#1198). The pass that notices it is due runs a few
    /// seconds late, so the strike is booked at its scheduled moment (the last strike plus one interval) and the
    /// next one is due one interval after that, not one interval after the late pass. Over a long spell the strikes
    /// stay exactly one interval apart. After a gap of a whole interval or more the schedule starts again at
    /// <paramref name="now"/>.
    /// </summary>
    public static DateTimeOffset StrikeLandedAt(DateTimeOffset lastStrikeUtc, DateTimeOffset now, TimeSpan checkInterval, TimeSpan outageOverlap)
    {
        var scheduled = lastStrikeUtc + checkInterval + outageOverlap;
        return now - scheduled < checkInterval ? scheduled : now;
    }

    /// <summary>
    /// The strike count a record shows (#1198): it never runs past the rule's own limit, so a limit of 3 reads
    /// "3 of 3" when the download is acted on, not "4 of 3" because the acting pass came a little later.
    /// </summary>
    public static int CapAtLimit(int strikes, int? ruleStrikeLimit)
        => ruleStrikeLimit is { } limit && limit >= 1 ? Math.Min(strikes, limit) : strikes;
}
