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
    bool IsSetupProblem = false,
    /// <summary>Percent complete (0-100), for a rule's completion-range scope (#905).</summary>
    double ProgressPercent = 0,
    /// <summary>Usenet has no peers or seeders, so peer/seed-based rules never apply to it.</summary>
    bool IsUsenetProtocol = false);

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
        IReadOnlyList<DownloadHealthRule> rules);
}
