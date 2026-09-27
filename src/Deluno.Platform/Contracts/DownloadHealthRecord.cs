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
    string Evidence);

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
    DateTimeOffset? IgnoredUntilUtc)
{
    public bool IsIgnored(DateTimeOffset now) => IgnoredUntilUtc is { } ignoredUntil && ignoredUntil > now;

    public bool BlocksCandidate(DateTimeOffset now, int threshold = 3)
        => DownloadHealthStrikes.Counts(Kind) && StrikeCount >= Math.Clamp(threshold, 1, 20) && !IsIgnored(now);
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

/// <summary>
/// What a strike is (#560).
///
/// <para><b>A strike is time, not a sighting.</b> One strike when a problem is
/// first seen, and one more for every half hour it is still there. A problem
/// that clears is over, and its strikes go with it. The first version counted
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
    /// <summary>How long a problem must last for each further strike.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

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

    /// <summary>Nothing has arrived for at least half an hour.</summary>
    public const string NoThroughput = "no-throughput";

    /// <summary>Slower than the configured floor for longer than allowed (#659, off by default).</summary>
    public const string LowSpeed = "low-speed";

    /// <summary>Fewer peers or seeders than the configured floor for longer than allowed (#659, off by default).</summary>
    public const string LowPeers = "low-peers";

    public static bool Counts(string kind)
        => string.Equals(kind, ClientStalled, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(kind, NoThroughput, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(kind, LowSpeed, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(kind, LowPeers, StringComparison.OrdinalIgnoreCase);

    /// <summary>Strikes for a problem seen without a break since <paramref name="since"/>.</summary>
    public static int For(DateTimeOffset since, DateTimeOffset now)
        => 1 + (int)Math.Clamp(Math.Floor((now - since) / Window), 0, 10_000);
}
