namespace Deluno.Contracts;

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
    bool IsSetupProblem = false);

public sealed record RecoveryHealthFinding(
    string Severity,
    string Kind,
    string Summary,
    string Evidence,
    string RecommendedAction,
    bool CanSafelyRetry,
    bool CanSafelyRemove);

/// <summary>
/// The configurable half of #659's download-health rules — how long a
/// download may go without meeting one before it counts as a problem, and
/// whether the rule runs at all. Cleanuparr makes the same thresholds a
/// setting rather than a constant, and a person's downloads differ too much
/// for one hard-coded answer to be right for everyone: a seedbox with deep
/// peer pools calls "no progress in 10 minutes" broken, a home line on a
/// small swarm calls it Tuesday.
///
/// <para>Every field has the default Deluno shipped with before it was
/// configurable, so an installation that never opens the settings screen
/// behaves exactly as it did (#659's original fix). Only "no progress" is on
/// by default; "too slow" and "not enough peers" are extra, sharper rules a
/// person opts into.</para>
/// </summary>
public sealed record DownloadHealthRulesSettings(
    bool NoProgressEnabled = true,
    int NoProgressMinutes = 30,
    bool MinSpeedEnabled = false,
    int MinSpeedKbps = 50,
    int MinSpeedMinutes = 30,
    bool MinPeersEnabled = false,
    int MinPeers = 1,
    int MinPeersMinutes = 30)
{
    public static readonly DownloadHealthRulesSettings Default = new();

    public TimeSpan NoProgressWindow => TimeSpan.FromMinutes(Math.Clamp(NoProgressMinutes, 1, 1440));
    public TimeSpan MinSpeedWindow => TimeSpan.FromMinutes(Math.Clamp(MinSpeedMinutes, 1, 1440));
    public TimeSpan MinPeersWindow => TimeSpan.FromMinutes(Math.Clamp(MinPeersMinutes, 1, 1440));
}

public interface IRecoveryHealthEvaluator
{
    IReadOnlyList<RecoveryHealthFinding> Evaluate(
        RecoveryQueueSnapshot item,
        DateTimeOffset capturedUtc,
        DownloadHealthRulesSettings rules);
}
