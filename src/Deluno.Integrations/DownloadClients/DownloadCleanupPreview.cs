using Deluno.Platform.Contracts;

namespace Deluno.Integrations.DownloadClients;

/// <summary>
/// A read-only explanation of the currently configured cleanup policy. It does
/// not execute cleanup; the policy executor remains responsible for ownership,
/// idempotency, audit, and the actual client capability.
/// </summary>
public sealed record DownloadCleanupPreview(
    string ClientId,
    string QueueItemId,
    string ReleaseName,
    string MatchedPolicy,
    string Reason,
    string ProposedAction,
    string AffectedFiles,
    bool RemovalAllowed,
    bool ReplacementSearchWillRun,
    bool RequiresReview,
    int StrikeThreshold = 3,
    bool BlocksRelease = false,
    bool PurgesPayload = false);

public static class DownloadCleanupPreviewBuilder
{
    public static DownloadCleanupPreview Create(DownloadQueueItem item)
        => Create(item, null);

    public static DownloadCleanupPreview Create(DownloadQueueItem item, PlatformSettingsSnapshot? settings)
    {
        var strikeThreshold = settings?.DownloadHealthStrikeThreshold ?? 3;
        var blockRelease = settings?.CleanupBlockReleaseAfterThreshold == true;
        var queueReplacement = settings?.CleanupQueueReplacementAfterThreshold == true;
        var removeClientEntry = settings?.CleanupRemoveClientEntryAfterThreshold == true;
        var purgePayload = settings?.CleanupPurgePayloadAfterThreshold == true;
        var finding = item.HealthFindings?.FirstOrDefault();
        var thresholdReached = finding is not null && finding.StrikeCount >= strikeThreshold;
        var configuredActions = new List<string>();
        if (blockRelease) configuredActions.Add("block the exact release");
        if (queueReplacement) configuredActions.Add("queue one bounded replacement search");
        if (removeClientEntry) configuredActions.Add("remove the client queue entry");
        if (purgePayload) configuredActions.Add("purge approved residual payload files");
        var proposedAction = thresholdReached && configuredActions.Count > 0
            ? $"At {strikeThreshold} strikes, the configured policy requests Deluno to {string.Join(", ", configuredActions)} after ownership checks."
            : $"This item remains in observation until it reaches {strikeThreshold} strike{(strikeThreshold == 1 ? string.Empty : "s")} or you take a manual action.";

        proposedAction += " Deluno will not remove an external-client item or its payload without proven ownership.";

        return new DownloadCleanupPreview(
            item.ClientId,
            item.Id,
            item.ReleaseName,
            configuredActions.Count == 0 ? "Observation and review" : $"Three-strike policy (threshold: {strikeThreshold})",
            finding is null ? "No active health finding is attached to this queue item." : $"{finding.Summary} {finding.Evidence}",
            proposedAction,
            item.SourcePath is null
                ? "No payload path was supplied by the download client."
                : "A payload path is known, but it is redacted here and will not be changed by this preview.",
            RemovalAllowed: thresholdReached && removeClientEntry,
            ReplacementSearchWillRun: thresholdReached && queueReplacement,
            RequiresReview: !thresholdReached || configuredActions.Count == 0,
            StrikeThreshold: strikeThreshold,
            BlocksRelease: thresholdReached && blockRelease,
            PurgesPayload: thresholdReached && purgePayload);
    }
}
