using Deluno.Contracts;
using Deluno.Platform.Contracts;

namespace Deluno.Recovery.Policies;

/// <summary>
/// Produces explainable, conservative health signals from a single telemetry snapshot.
/// It intentionally has no side effects: retention and removal require a separate,
/// audited policy and explicit user approval.
/// </summary>
public sealed class DownloadHealthEvaluator : IRecoveryHealthEvaluator
{
    private static readonly TimeSpan ExcessiveEta = TimeSpan.FromDays(7);
    private static readonly string[] SuspiciousExtensions = [".bat", ".cmd", ".exe", ".js", ".lnk", ".ps1", ".scr", ".url", ".vbs"];

    public IReadOnlyList<RecoveryHealthFinding> Evaluate(RecoveryQueueSnapshot item, DateTimeOffset capturedUtc)
        => Evaluate(item, capturedUtc, DownloadHealthRules.Defaults);

    public IReadOnlyList<RecoveryHealthFinding> Evaluate(RecoveryQueueSnapshot item, DateTimeOffset capturedUtc, IReadOnlyList<DownloadHealthRule> rules)
    {
        var findings = new List<RecoveryHealthFinding>();

        if (item.IsSetupProblem)
        {
            // #880/#881: a full disk, an I/O error, a permissions problem, or
            // files missing from disk is a problem on this machine, not a
            // dead release. It stays visible until fixed, but it never earns
            // a strike, and DownloadHealthStrikes.Counts excludes its Kind so
            // it can never blocklist, replace, remove from the client, or
            // delete anything on its own.
            findings.Add(new(
                Severity: "critical",
                Kind: DownloadHealthStrikes.ClientSetupProblem,
                Summary: "This looks like a problem on your machine, not the release.",
                Evidence: string.IsNullOrWhiteSpace(item.ErrorMessage) ? $"Queue status: {item.Status}." : item.ErrorMessage,
                RecommendedAction: "Check disk space, folder permissions, and that the client can reach its files, then Resume or Recheck once it's fixed.",
                CanSafelyRetry: true,
                CanSafelyRemove: false));
        }
        else if (item.Status == "stalled" || !string.IsNullOrWhiteSpace(item.ErrorMessage))
        {
            findings.Add(new(
                Severity: "critical",
                Kind: DownloadHealthStrikes.ClientStalled,
                Summary: "Download client reported this item as stalled or errored.",
                Evidence: string.IsNullOrWhiteSpace(item.ErrorMessage) ? $"Queue status: {item.Status}." : item.ErrorMessage,
                RecommendedAction: "Inspect the client error, then use Recheck or Resume if the client supports it.",
                CanSafelyRetry: true,
                CanSafelyRemove: false));
        }

        if (item.Status == "importFailed")
        {
            findings.Add(new(
                Severity: "critical",
                Kind: "import-failed",
                Summary: "Deluno could not import the completed download.",
                Evidence: item.SourcePath is null ? "No import source path was recorded." : $"Import source: {item.SourcePath}",
                RecommendedAction: "Open the recovery details and preview the destination before retrying the import.",
                CanSafelyRetry: true,
                CanSafelyRemove: false));
        }

        if (item.Status == "processingFailed")
        {
            findings.Add(new(
                Severity: "critical",
                Kind: "post-processing-failed",
                Summary: "The configured post-processing step failed before import.",
                Evidence: string.IsNullOrWhiteSpace(item.ErrorMessage) ? "The processor did not produce an import-ready output." : item.ErrorMessage,
                RecommendedAction: "Review the processor error and its output path before retrying.",
                CanSafelyRetry: true,
                CanSafelyRemove: false));
        }

        if (item.Status is "importReady" or "importQueued" && string.IsNullOrWhiteSpace(item.SourcePath))
        {
            findings.Add(new(
                Severity: "warning",
                Kind: "missing-import-path",
                Summary: "This download is ready, but Deluno does not know where to import it from.",
                Evidence: "The download client snapshot did not provide a source path.",
                RecommendedAction: "Check the client path mapping, then refresh the queue before creating a manual import.",
                CanSafelyRetry: false,
                CanSafelyRemove: false));
        }

        var noProgress = MatchRule(rules, DownloadHealthRuleKinds.NoProgress, item);
        if (noProgress is not null &&
            item.Status == "downloading" &&
            item.SpeedMbps <= 0 &&
            capturedUtc - item.AddedUtc >= noProgress.Timer)
        {
            findings.Add(RuleFinding(
                noProgress,
                "warning",
                DownloadHealthStrikes.NoThroughput,
                "This download has no observed throughput.",
                $"0 MB/s after at least {noProgress.Timer.TotalMinutes:0} minutes in the queue.",
                "Check peers, the indexer/client connection, and then Resume or Recheck if appropriate."));
        }

        // Off by default (#659): a person opts into a sharper floor than "zero
        // throughput" once they know their own swarms - a download crawling
        // under a few KB/s for an hour is, for them, as good as stalled.
        var tooSlow = MatchRule(rules, DownloadHealthRuleKinds.TooSlow, item);
        if (tooSlow is not null &&
            item.Status == "downloading" &&
            item.SpeedMbps * 1000 < tooSlow.Threshold &&
            capturedUtc - item.AddedUtc >= tooSlow.Timer)
        {
            findings.Add(RuleFinding(
                tooSlow,
                "warning",
                DownloadHealthStrikes.LowSpeed,
                "This download is slower than the configured floor.",
                $"{item.SpeedMbps * 1000:0.#} KB/s, under {tooSlow.Threshold:0} KB/s, for at least {tooSlow.Timer.TotalMinutes:0} minutes.",
                "Check the indexer's health and the client's connection, then Resume or Recheck if appropriate."));
        }

        // Off by default (#659): the same idea for peer/seeder count - a
        // download with plenty of speed today can still be one seeder from
        // never finishing. Usenet has no peers, so this never applies there.
        var lowPeers = MatchRule(rules, DownloadHealthRuleKinds.LowPeers, item);
        if (lowPeers is not null &&
            !item.IsUsenetProtocol &&
            item.Status == "downloading" &&
            item.Peers < lowPeers.Threshold &&
            capturedUtc - item.AddedUtc >= lowPeers.Timer)
        {
            findings.Add(RuleFinding(
                lowPeers,
                "warning",
                DownloadHealthStrikes.LowPeers,
                "This download has fewer peers or seeders than the configured floor.",
                $"{item.Peers} peers, under {lowPeers.Threshold:0}, for at least {lowPeers.Timer.TotalMinutes:0} minutes.",
                "Check whether the release still has a healthy swarm; a replacement may do better."));
        }

        // #818: a torrent with nobody left to seed it is not "slow", it is
        // dead - a separate, sharper check from the configurable low-peers
        // floor above, with its own grace period so a magnet that has not
        // yet announced is not judged the instant it is added. A tracker
        // explicitly saying the torrent is unregistered is the same verdict
        // by a different route, so it trips this rule too - a temporary
        // "tracker unreachable" read never sets TrackerStatus to Unregistered
        // (it stays Unknown/Ok), so an outage cannot be mistaken for this.
        var deadTorrent = MatchRule(rules, DownloadHealthRuleKinds.DeadTorrent, item);
        var trackerUnregistered = item.TrackerStatus == DownloadTrackerStatuses.Unregistered;
        if (deadTorrent is not null &&
            !item.IsUsenetProtocol &&
            item.Status == "downloading" &&
            (item.Peers <= 0 || trackerUnregistered) &&
            capturedUtc - item.AddedUtc >= TimeSpan.FromMinutes(deadTorrent.Threshold) &&
            capturedUtc - item.AddedUtc >= deadTorrent.Timer)
        {
            findings.Add(RuleFinding(
                deadTorrent,
                "warning",
                DownloadHealthStrikes.DeadTorrent,
                trackerUnregistered
                    ? "This torrent's tracker reports it unregistered or removed."
                    : "This torrent has no seeds or peers left.",
                trackerUnregistered
                    ? $"Tracker reports this torrent unregistered, past its {deadTorrent.Threshold:0}-minute grace period."
                    : $"0 peers for at least {deadTorrent.Timer.TotalMinutes:0} minutes, past its {deadTorrent.Threshold:0}-minute grace period.",
                "This release may no longer be available anywhere. A replacement search may find a healthier one."));
        }

        // #878, cleanup sheet C5 (the owner's decided behaviour): the tracker
        // itself saying a torrent is unregistered is treated as certain, not
        // merely suspicious - unlike every rule above, this is not something
        // a person opts into, and it does not wait for repeated sightings.
        // RuleStrikeLimit of 1 is what makes the very first observation act;
        // RuleActionPreset is left unset so the platform's own cleanup
        // switches (#936) still decide whether anything actually removes,
        // deletes, blocklists or searches.
        if (trackerUnregistered && !item.IsUsenetProtocol && item.Status != "importReady")
        {
            findings.Add(new(
                Severity: "critical",
                Kind: DownloadHealthStrikes.TrackerUnregistered,
                Summary: "The tracker says this torrent is unregistered or removed.",
                Evidence: "The tracker rejected this torrent's announce as unregistered, not merely unreachable.",
                RecommendedAction: "Deluno treats this release as dead: it removes it from the client with its files, blocklists it, and searches again, once your cleanup switches are on.",
                CanSafelyRetry: false,
                CanSafelyRemove: true,
                RuleStrikeLimit: 1,
                RuleTimerMinutes: 1));
        }

        // Only a download still downloading has a completion time. qBittorrent
        // reports 8640000 seconds, its "never", for one that is finished and
        // seeding, and every finished torrent was flagged (#523).
        if (item.Status == "downloading" && item.EtaSeconds > ExcessiveEta.TotalSeconds)
        {
            findings.Add(new(
                Severity: "warning",
                Kind: "excessive-eta",
                Summary: "This download's estimated completion time is unusually long.",
                Evidence: $"Client ETA: {TimeSpan.FromSeconds(item.EtaSeconds):d\\.hh\\:mm\\:ss}.",
                RecommendedAction: "Review availability and peers before replacing or retrying the release.",
                CanSafelyRetry: false,
                CanSafelyRemove: false));
        }

        if (SuspiciousExtensions.Any(extension => item.ReleaseName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
        {
            findings.Add(new(
                Severity: "warning",
                Kind: "suspicious-payload-name",
                Summary: "The release name ends in an executable or shortcut extension.",
                Evidence: $"Release name: {item.ReleaseName}",
                RecommendedAction: "Verify the download contents before any import. Deluno will not remove it automatically.",
                CanSafelyRetry: false,
                CanSafelyRemove: false));
        }

        return findings;
    }

    /// <summary>
    /// The one enabled rule of <paramref name="kind"/> that covers this item's
    /// protocol and completion percent, if any. <see cref="DownloadHealthRuleValidator"/>
    /// refuses to save two enabled rules of the same kind whose scope could
    /// both match the same download, so at most one ever matches here.
    /// </summary>
    private static DownloadHealthRule? MatchRule(IReadOnlyList<DownloadHealthRule> rules, string kind, RecoveryQueueSnapshot item)
        => rules.FirstOrDefault(rule =>
            rule.Enabled &&
            string.Equals(rule.Kind, kind, StringComparison.OrdinalIgnoreCase) &&
            rule.AppliesToProtocol(item.IsUsenetProtocol) &&
            rule.CoversCompletion(item.ProgressPercent) &&
            rule.AppliesToPrivacy(item.IsPrivate));

    private static RecoveryHealthFinding RuleFinding(
        DownloadHealthRule rule,
        string severity,
        string strikeKind,
        string summary,
        string evidence,
        string recommendedAction)
        => new(
            Severity: severity,
            Kind: strikeKind,
            Summary: summary,
            Evidence: evidence,
            RecommendedAction: recommendedAction,
            CanSafelyRetry: true,
            CanSafelyRemove: false,
            RuleId: rule.Id,
            RuleStrikeLimit: rule.StrikeLimit,
            RuleTimerMinutes: rule.TimerMinutes,
            RuleActionPreset: rule.ActionPreset);
}
