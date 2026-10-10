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

    public IReadOnlyList<RecoveryHealthFinding> Evaluate(RecoveryQueueSnapshot item, DateTimeOffset capturedUtc)
        => Evaluate(item, capturedUtc, DownloadHealthRules.Defaults, null);

    public IReadOnlyList<RecoveryHealthFinding> Evaluate(
        RecoveryQueueSnapshot item,
        DateTimeOffset capturedUtc,
        IReadOnlyList<DownloadHealthRule> rules,
        ContentBlocklistSettings? contentBlocklist = null)
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
        else if (!DownloadHealthStrikes.IsFinishedOrHandedOff(item.Status, item.ProgressPercent)
            && (item.Status == "stalled" || (!string.IsNullOrWhiteSpace(item.ErrorMessage) && !IsTransferring(item))))
        {
            // Whether this platform-wide fallback stays is decided once every rule has spoken (end of Evaluate):
            // when a person's own strike rule is already striking this download, that rule's strikes, check
            // interval and action decide, and the fallback neither duplicates it nor takes it over (#1194, #1205).
            findings.Add(new(
                Severity: "critical",
                Kind: DownloadHealthStrikes.ClientStalled,
                Summary: "Download client reported this item as stalled or errored.",
                Evidence: string.IsNullOrWhiteSpace(item.ErrorMessage) ? $"Queue status: {item.Status}." : item.ErrorMessage,
                RecommendedAction: "Inspect the client error, then use Recheck or Resume if the client supports it.",
                CanSafelyRetry: true,
                CanSafelyRemove: false));

            // #951 (off by default, #936): the same client-reported failure as
            // the unconditional finding just above, but only struck through
            // the rule engine once a person configures this rule's own
            // strikes, timer, scope and action. The unconditional finding
            // above is untouched - it still shows, and still falls back to
            // the platform-wide strike threshold on Search & Cycles - so
            // nothing about an existing install's behaviour changes merely
            // because this kind now exists.
            var clientFailure = MatchRule(rules, DownloadHealthRuleKinds.ClientFailure, item);
            if (clientFailure is not null)
            {
                findings.Add(RuleFinding(
                    clientFailure,
                    "critical",
                    DownloadHealthStrikes.ClientFailure,
                    "Download client reported this item as stalled or errored.",
                    string.IsNullOrWhiteSpace(item.ErrorMessage) ? $"Queue status: {item.Status}." : item.ErrorMessage,
                    "Deluno can act on this rule's own schedule - check its configured action before relying on it."));
            }
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
        // Still counted once the no-progress window has flipped the status to stalled (#1193): the person's rule
        // keeps its own strikes through the flip instead of handing them to the platform-wide fallback.
        // A strike per check: nothing arriving at this check is one strike, however old the download is.
        var noProgressFires = noProgress is not null &&
            ReadsAsDownloading(item) &&
            NoBytesArrived(item);
        if (noProgressFires)
        {
            findings.Add(RuleFinding(
                noProgress!,
                "warning",
                DownloadHealthStrikes.NoThroughput,
                "This download has no observed throughput.",
                string.IsNullOrWhiteSpace(item.ErrorMessage) ? "No new data had arrived since the previous check." : item.ErrorMessage,
                "Check peers, the indexer/client connection, and then Resume or Recheck if appropriate."));
        }

        // Off by default (#659): a person opts into a sharper floor than "zero
        // throughput" once they know their own swarms - a download crawling
        // under a few KB/s for an hour is, for them, as good as stalled.
        //
        // #951 adds three options, all opt-in and all null/false by default
        // so an existing "too slow" rule keeps behaving exactly as before:
        // a hard ceiling on total time in queue regardless of speed
        // (TooSlowMaxTotalMinutes), a size above which this rule never
        // applies at all (IgnoreAboveSizeBytes, parity sheet G157), and
        // skipping entirely while the client's own alternate/scheduled speed
        // limit is on (SkipWhileAlternateSpeedLimitOn - a deliberately
        // throttled download is not unhealthy).
        var tooSlow = MatchRule(rules, DownloadHealthRuleKinds.TooSlow, item);
        if (tooSlow is not null &&
            ReadsAsDownloading(item) &&
            !(tooSlow.SkipWhileAlternateSpeedLimitOn && item.AlternateSpeedLimitOn) &&
            !(tooSlow.IgnoreAboveSizeBytes is { } ignoreAboveSize && ignoreAboveSize > 0 && item.SizeBytes > ignoreAboveSize))
        {
            var elapsed = capturedUtc - item.AddedUtc;
            // A download moving nothing at all belongs to the "No progress" rule, never counted by both (#1194).
            var underFloorTooLong = item.SpeedMbps > 0 && item.SpeedMbps * 1000 < tooSlow.Threshold && !noProgressFires;
            var overMaxTotalTime = !noProgressFires && tooSlow.TooSlowMaxTotalMinutes is { } maxTotalMinutes &&
                maxTotalMinutes > 0 &&
                elapsed >= TimeSpan.FromMinutes(maxTotalMinutes);

            if (underFloorTooLong || overMaxTotalTime)
            {
                findings.Add(RuleFinding(
                    tooSlow,
                    "warning",
                    DownloadHealthStrikes.LowSpeed,
                    "This download is slower than the configured floor.",
                    underFloorTooLong
                        ? $"{item.SpeedMbps * 1000:0.#} KB/s at this check, under the limit of {tooSlow.Threshold:0} KB/s."
                        : $"Still downloading after {elapsed.TotalMinutes:0} minutes, past its {tooSlow.TooSlowMaxTotalMinutes:0}-minute maximum total time, whatever its current speed.",
                    "Check the indexer's health and the client's connection, then Resume or Recheck if appropriate."));
            }
        }

        // #951, qBittorrent only (its own metaDL state): a magnet that has
        // not resolved its own name, file list or size yet. Off by default
        // (#936); never applies to Usenet, which has no magnets.
        var magnetMetadata = MatchRule(rules, DownloadHealthRuleKinds.MagnetMetadata, item);
        if (magnetMetadata is not null &&
            !item.IsUsenetProtocol &&
            item.IsFetchingMetadata)
        {
            findings.Add(RuleFinding(
                magnetMetadata,
                "warning",
                DownloadHealthStrikes.MagnetMetadata,
                "This magnet has not finished fetching its metadata.",
                "The magnet had not finished fetching its metadata at this check.",
                "The magnet's DHT/peer exchange may be unreachable; a replacement search may find a direct torrent instead."));
        }

        // Off by default (#659): the same idea for peer/seeder count - a
        // download with plenty of speed today can still be one seeder from
        // never finishing. Usenet has no peers, so this never applies there.
        var lowPeers = MatchRule(rules, DownloadHealthRuleKinds.LowPeers, item);
        // Stalled counts too (#1205): the stall flip turns a starved download into "stalled" after one check
        // interval, and that must not end the peers rule's strikes.
        if (lowPeers is not null &&
            !item.IsUsenetProtocol &&
            ReadsAsDownloading(item) &&
            item.Peers < lowPeers.Threshold)
        {
            findings.Add(RuleFinding(
                lowPeers,
                "warning",
                DownloadHealthStrikes.LowPeers,
                "This download has fewer peers or seeders than the configured floor.",
                $"{item.Peers} peers at this check, under the floor of {lowPeers.Threshold:0}.",
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
            ReadsAsDownloading(item) &&
            (item.Peers <= 0 || trackerUnregistered) &&
            capturedUtc - item.AddedUtc >= TimeSpan.FromMinutes(deadTorrent.Threshold))
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
                    : $"0 peers at this check, past its {deadTorrent.Threshold:0}-minute grace period.",
                "This release may no longer be available anywhere. A replacement search may find a healthier one."));
        }

        // #878, cleanup sheet C5 (James's decided behaviour): the tracker
        // itself saying a torrent is unregistered is treated as certain, not
        // merely suspicious - unlike every rule above, this is not something
        // a person opts into, and it does not wait for repeated sightings.
        // RuleStrikeLimit of 1 is what makes the very first observation act;
        // RuleActionPreset is left unset so the platform's own cleanup
        // switches (#936) still decide whether anything actually removes,
        // deletes, blocklists or searches.
        if (trackerUnregistered && !item.IsUsenetProtocol && !DownloadHealthStrikes.IsFinishedOrHandedOff(item.Status, item.ProgressPercent))
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

        // #951: a download sitting at 0% always says why, whatever rules are
        // configured - the same always-on treatment as "client-stalled"
        // above and "excessive-eta" below, never gated behind a switch,
        // because a person should never have to guess why nothing has
        // arrived. DefaultWindow (30 minutes) is the same grace a freshly
        // added download already gets elsewhere before anything is said.
        if (ReadsAsDownloading(item) &&
            item.ProgressPercent <= 0 &&
            capturedUtc - item.AddedUtc >= DownloadHealthStrikes.DefaultWindow)
        {
            findings.Add(new(
                Severity: "warning",
                Kind: "zero-percent-download",
                Summary: "This download is still at 0%.",
                Evidence: ZeroPercentEvidence(item, capturedUtc),
                RecommendedAction: "Check the indexer/client connection, then Resume or Recheck if appropriate; a replacement search may do better.",
                CanSafelyRetry: true,
                CanSafelyRemove: false));
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

        // #811/#970: qBittorrent's own availability reading - below 1 means
        // no single peer holds a complete copy of this torrent right now, so
        // it may stall regardless of how many peers are connected. Only
        // meaningful while still downloading (a finished torrent's
        // availability says nothing about whether it finished), and only
        // when the client actually reports it - Usenet and clients that do
        // not read this field stay null, which never trips it.
        if (ReadsAsDownloading(item) && item.Availability is { } availability && availability < 1)
        {
            findings.Add(new(
                Severity: "warning",
                Kind: DownloadHealthStrikes.LowAvailability,
                Summary: "No peer has a complete copy of this release yet.",
                Evidence: $"{availability:0.00} availability (1.0 is one full copy across all connected peers).",
                RecommendedAction: "A replacement search may find a release with fuller availability.",
                CanSafelyRetry: false,
                CanSafelyRemove: false));
        }

        // #816: check the files actually inside the download against the
        // configured content blocklist, rather than only the release's own
        // name. When no file listing was fetched (the client does not expose
        // one, or the feature is off), fall back to matching the release name
        // itself against the same patterns - this is what keeps the old
        // release-name extension check alive as a special case of the new,
        // configurable one instead of a second parallel check.
        if (contentBlocklist is { Enabled: true })
        {
            var candidates = item.Files is { Count: > 0 } files ? files : [item.ReleaseName];
            var offendingFile = ContentBlocklistMatcher.FindOffendingFile(candidates, contentBlocklist);
            if (offendingFile is not null)
            {
                findings.Add(new(
                    Severity: "warning",
                    Kind: "content-blocklist-match",
                    Summary: "A file inside this download matches the content blocklist.",
                    Evidence: $"Offending file: {offendingFile}",
                    RecommendedAction: "Verify the download contents before any import. Turn on the dangerous-file rule to have Deluno act on this automatically.",
                    CanSafelyRetry: false,
                    CanSafelyRemove: true));

                // #879: the same match, but only struck and acted on when the
                // dedicated rule (its own strike limit, timer and action
                // preset, off by default) is enabled for this download's
                // scope - one instant strike is enough; a dangerous file
                // does not need to be seen twice.
                var dangerousFile = MatchRule(rules, DownloadHealthRuleKinds.DangerousFile, item);
                if (dangerousFile is not null)
                {
                    findings.Add(RuleFinding(
                        dangerousFile,
                        "critical",
                        DownloadHealthStrikes.DangerousFile,
                        "A file inside this download matches the content blocklist.",
                        $"Offending file: {offendingFile}",
                        "Deluno can remove this download with its data, blocklist the release, and search again."));
                }
            }
        }

        return OneStrikePerDownload(findings);
    }

    /// <summary>
    /// A download that is still being fetched: downloading, or stalled (#1205). The stall flip turns a download that
    /// gained no bytes for a check interval into "stalled", and a rule about getting the bytes (peers, speed, a dead
    /// swarm) must keep judging it through that flip, not lose it.
    /// </summary>
    private static bool ReadsAsDownloading(RecoveryQueueSnapshot item)
        => item.Status is "downloading" or "stalled" &&
           !DownloadHealthStrikes.IsFinishedOrHandedOff(item.Status, item.ProgressPercent);

    /// <summary>
    /// Nothing arrived since the previous check (#1207): the downloaded bytes did not grow, whatever speed the client
    /// reports (Deluge shows 0.000057 MB/s on a stuck torrent). With no earlier reading to compare to (first sighting,
    /// or a caller that keeps no history) it falls back to the reported speed.
    /// </summary>
    private static bool NoBytesArrived(RecoveryQueueSnapshot item)
        => item.BytesGrewSinceLastCheck is { } grew ? !grew : item.SpeedMbps <= 0;

    /// <summary>
    /// The rules that strike a download for not getting its bytes, most specific first. One download is struck by
    /// one of them per check (#1205): when several match, the first here keeps the strike and the rest stand down,
    /// so the removal can name the one rule that reached its count. If it recovers, its strikes reset and the next
    /// rule that matches starts its own.
    /// </summary>
    private static readonly string[] TransferRulePriority =
    [
        DownloadHealthStrikes.DeadTorrent,
        DownloadHealthStrikes.MagnetMetadata,
        DownloadHealthStrikes.LowPeers,
        DownloadHealthStrikes.LowSpeed,
        DownloadHealthStrikes.ClientFailure,
        DownloadHealthStrikes.NoThroughput
    ];

    private static bool IsTransferRuleFinding(RecoveryHealthFinding finding)
        => finding.RuleId is not null && TransferRulePriority.Contains(finding.Kind, StringComparer.OrdinalIgnoreCase);

    private static List<RecoveryHealthFinding> OneStrikePerDownload(List<RecoveryHealthFinding> findings)
    {
        var owner = TransferRulePriority
            .Select(kind => findings.FirstOrDefault(finding => IsTransferRuleFinding(finding) && string.Equals(finding.Kind, kind, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(finding => finding is not null);
        if (owner is null)
        {
            return findings;
        }

        // The platform-wide "client stalled" fallback yields to the rule that owns this download: it neither
        // duplicates the strike nor takes the download from a rule that is already counting it.
        return findings
            .Where(finding => ReferenceEquals(finding, owner)
                || (!IsTransferRuleFinding(finding)
                    && !string.Equals(finding.Kind, DownloadHealthStrikes.ClientStalled, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>
    /// Bytes are arriving right now. A tracker that timed out, or any other
    /// error text beside a download that is moving, is not a stalled download
    /// (rig, 8 Oct: "Error: timed out" on a Deluge torrent running at 8 MB/s).
    /// </summary>
    private static bool IsTransferring(RecoveryQueueSnapshot item)
        => item.SpeedMbps > 0 && string.Equals(item.Status, "downloading", StringComparison.OrdinalIgnoreCase);

    /// <summary>#951: the plain-English reason behind a 0% download, from whichever signal Deluno actually has for it.</summary>
    private static string ZeroPercentEvidence(RecoveryQueueSnapshot item, DateTimeOffset capturedUtc)
    {
        var minutes = (capturedUtc - item.AddedUtc).TotalMinutes;
        if (item.IsFetchingMetadata)
        {
            return $"Still fetching the torrent's own metadata after {minutes:0} minutes; no data can arrive until that finishes.";
        }

        if (!item.IsUsenetProtocol && item.Peers <= 0)
        {
            return $"No peers connected after {minutes:0} minutes.";
        }

        return $"No bytes have arrived after {minutes:0} minutes.";
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
