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
        => Evaluate(item, capturedUtc, DownloadHealthRulesSettings.Default);

    public IReadOnlyList<RecoveryHealthFinding> Evaluate(RecoveryQueueSnapshot item, DateTimeOffset capturedUtc, DownloadHealthRulesSettings rules)
    {
        var findings = new List<RecoveryHealthFinding>();

        if (item.Status == "stalled" || !string.IsNullOrWhiteSpace(item.ErrorMessage))
        {
            findings.Add(new(
                Severity: "critical",
                Kind: "client-stalled",
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

        if (rules.NoProgressEnabled &&
            item.Status == "downloading" &&
            item.SpeedMbps <= 0 &&
            capturedUtc - item.AddedUtc >= rules.NoProgressWindow)
        {
            findings.Add(new(
                Severity: "warning",
                Kind: DownloadHealthStrikes.NoThroughput,
                Summary: "This download has no observed throughput.",
                Evidence: $"0 MB/s after at least {rules.NoProgressWindow.TotalMinutes:0} minutes in the queue.",
                RecommendedAction: "Check peers, the indexer/client connection, and then Resume or Recheck if appropriate.",
                CanSafelyRetry: true,
                CanSafelyRemove: false));
        }

        // Off by default (#659): a person opts into a sharper floor than "zero
        // throughput" once they know their own swarms - a download crawling
        // under a few KB/s for an hour is, for them, as good as stalled.
        if (rules.MinSpeedEnabled &&
            item.Status == "downloading" &&
            item.SpeedMbps * 1000 < rules.MinSpeedKbps &&
            capturedUtc - item.AddedUtc >= rules.MinSpeedWindow)
        {
            findings.Add(new(
                Severity: "warning",
                Kind: DownloadHealthStrikes.LowSpeed,
                Summary: "This download is slower than the configured floor.",
                Evidence: $"{item.SpeedMbps * 1000:0.#} KB/s, under {rules.MinSpeedKbps} KB/s, for at least {rules.MinSpeedWindow.TotalMinutes:0} minutes.",
                RecommendedAction: "Check the indexer's health and the client's connection, then Resume or Recheck if appropriate.",
                CanSafelyRetry: true,
                CanSafelyRemove: false));
        }

        // Off by default (#659): the same idea for peer/seeder count - a
        // download with plenty of speed today can still be one seeder from
        // never finishing.
        if (rules.MinPeersEnabled &&
            item.Status == "downloading" &&
            item.Peers < rules.MinPeers &&
            capturedUtc - item.AddedUtc >= rules.MinPeersWindow)
        {
            findings.Add(new(
                Severity: "warning",
                Kind: DownloadHealthStrikes.LowPeers,
                Summary: "This download has fewer peers or seeders than the configured floor.",
                Evidence: $"{item.Peers} peers, under {rules.MinPeers}, for at least {rules.MinPeersWindow.TotalMinutes:0} minutes.",
                RecommendedAction: "Check whether the release still has a healthy swarm; a replacement may do better.",
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
}
