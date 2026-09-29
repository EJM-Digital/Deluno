using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Notifications;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging;

namespace Deluno.Integrations.DownloadClients;

/// <param name="Cleared">Refusals whose download client has now forgotten them.</param>
/// <param name="WaitingOnSharing">
/// Refusals left alone because the sharing rule still owns that copy. Not a
/// failure — the rule knows what the site expects and this does not.
/// </param>
/// <param name="Failed">Refusals whose client could not be reached. Tried again next time.</param>
public sealed record RefusedDownloadCleanupResult(int Cleared, int WaitingOnSharing, int Failed);

/// <summary>What happened to one refusal, in a word the screen can say back.</summary>
public static class RefusedDownloadCleanupOutcomes
{
    public const string Cleared = "cleared";

    /// <summary>Nothing was left to clear — no client, or already done.</summary>
    public const string NothingToClear = "nothingToClear";

    /// <summary>The tracker still expects the seed.</summary>
    public const string StillSharing = "stillSharing";

    /// <summary>The client did not answer, or refused.</summary>
    public const string ClientUnavailable = "clientUnavailable";

    public const string NotFound = "notFound";

    /// <summary>Deluno never sent this download, so it is left in the client for its owner.</summary>
    public const string NotSentByDeluno = "notSentByDeluno";

    /// <summary>
    /// A release the client itself reported as failed (#882/#883/#885) is
    /// left exactly where it is, because the user's cleanup setting is off
    /// (#936: off by default, and an existing install is never flipped on).
    /// </summary>
    public const string CleanupDisabled = "cleanupDisabled";
}

public interface IRefusedDownloadCleanupService
{
    Task<RefusedDownloadCleanupResult> CleanUpEverythingAsync(CancellationToken cancellationToken);

    Task<string> CleanUpOneAsync(string blockedReleaseId, CancellationToken cancellationToken);
}

/// <summary>
/// Clearing up after a release Deluno has refused.
///
/// <para>James found the hole this fills: <i>"if we are refusing something, is
/// it being deleted and cleaned up so there are no traces of it"</i>. It was
/// not — a refused copy kept costing disk, kept sitting in the client's queue,
/// and the client kept remembering it, so the day you un-refused it the client
/// would silently decline to fetch it again.</para>
///
/// <para><b>It asks the client to forget, not to delete.</b> On a torrent
/// client those are one request; on SABnzbd and NZBGet, forgetting also clears
/// the history that outlives the transfer, which is the half that lets the
/// release back in.</para>
///
/// <para><b>And it waits for the sharing rule.</b> A copy still under a hold is
/// left alone and tried again later — the rule knows how long the site the
/// release came from expects you to keep seeding, and this does not.</para>
///
/// <para>A service rather than a method on the worker, because the schedule and
/// the <b>Clean up now</b> button on a blocklist row have to do the identical
/// thing. DESIGN-007: "nothing automatic is only automatic".</para>
///
/// <para><b>A release the client itself reported as failed is different
/// (#882/#883/#885, cleanup sheet D1/D2/D4).</b> Deluno already knows those
/// files are junk, but destroying a download without being asked is exactly
/// what #936 says nothing does by default: it only clears one once the
/// user's own "remove a bad download" and "delete a bad download's files"
/// switches (<see cref="Deluno.Platform.Contracts.PlatformSettingsSnapshot.CleanupRemoveClientEntryAfterThreshold"/>
/// and <see cref="Deluno.Platform.Contracts.PlatformSettingsSnapshot.CleanupPurgePayloadAfterThreshold"/>,
/// the same pair the setup guide already asks about) are both on. Off, the
/// refusal is left exactly where it is — still visible and still explained
/// on the Blocklist screen, which is where "the screen says so" is already
/// satisfied for a refused release.</para>
/// </summary>
public sealed class RefusedDownloadCleanupService(
    IBlockedReleaseRepository blockedReleases,
    IDownloadClientTelemetryService downloadClients,
    IDownloadSharingRepository sharingRepository,
    Deluno.Platform.Data.IPlatformSettingsRepository platformRepository,
    IActivityFeedRepository activityFeedRepository,
    ILogger<RefusedDownloadCleanupService> logger,
    IOutboundNotificationService? outboundNotificationService = null,
    INotificationService? notificationPreferences = null,
    // #990: the cleared release's own title tags, so a tagged notification
    // connection fires for a cleanup scoped to it.
    IMediaTagStore? mediaTagStore = null) : IRefusedDownloadCleanupService
{
    public async Task<RefusedDownloadCleanupResult> CleanUpEverythingAsync(CancellationToken cancellationToken)
    {
        var pending = await blockedReleases.ListAwaitingCleanupAsync(await ShouldDeletePayloadAsync(cancellationToken), cancellationToken);
        if (pending.Count == 0)
        {
            return new RefusedDownloadCleanupResult(0, 0, 0);
        }

        var holds = await SharingHoldsAsync(cancellationToken);
        var clientReportedFailureCleanupOn = await ClientReportedFailureCleanupIsOnAsync(cancellationToken);
        var cleared = 0;
        var waiting = 0;
        var failed = 0;

        foreach (var release in pending)
        {
            switch (await ClearAsync(release, holds, clientReportedFailureCleanupOn, cancellationToken))
            {
                case RefusedDownloadCleanupOutcomes.Cleared:
                    cleared++;
                    break;
                case RefusedDownloadCleanupOutcomes.StillSharing:
                    waiting++;
                    break;
                case RefusedDownloadCleanupOutcomes.ClientUnavailable:
                    failed++;
                    break;
            }
        }

        if (cleared > 0 || waiting > 0)
        {
            logger.LogInformation(
                "Cleared {ClearedCount} refused download(s); {WaitingCount} still held by the sharing rule.",
                cleared,
                waiting);
        }

        return new RefusedDownloadCleanupResult(cleared, waiting, failed);
    }

    /// <summary>
    /// One row, by hand. Reads the whole list rather than fetching by id
    /// because the same filter decides what is <em>eligible</em> — a refusal
    /// whose reason does not justify destroying the file is not one a button
    /// should be able to destroy either.
    /// </summary>
    public async Task<string> CleanUpOneAsync(string blockedReleaseId, CancellationToken cancellationToken)
    {
        var release = (await blockedReleases.ListAwaitingCleanupAsync(await ShouldDeletePayloadAsync(cancellationToken), cancellationToken))
            .FirstOrDefault(candidate => string.Equals(candidate.Id, blockedReleaseId, StringComparison.Ordinal));

        if (release is null)
        {
            // Either it does not exist, it is already clean, or its reason
            // never justified clearing. All three mean the same to the caller:
            // there is nothing here to do.
            return (await blockedReleases.ListAsync(cancellationToken))
                .Any(candidate => string.Equals(candidate.Id, blockedReleaseId, StringComparison.Ordinal))
                ? RefusedDownloadCleanupOutcomes.NothingToClear
                : RefusedDownloadCleanupOutcomes.NotFound;
        }

        return await ClearAsync(
            release,
            await SharingHoldsAsync(cancellationToken),
            await ClientReportedFailureCleanupIsOnAsync(cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// The shipped table, with a still-packed archive folded in only when the
    /// person has turned that on (#887). Everything else the table already
    /// says to clear is untouched by this setting.
    /// </summary>
    private async Task<Func<string, bool>> ShouldDeletePayloadAsync(CancellationToken cancellationToken)
    {
        var settings = await platformRepository.GetAsync(cancellationToken);
        if (!settings.RemoveAndDeletePackedArchivesFromClient)
        {
            return ImportFailurePolicy.ShouldDeletePayload;
        }

        return reasonCode =>
            ImportFailurePolicy.ShouldDeletePayload(reasonCode) ||
            reasonCode == ImportFailurePolicy.UnsupportedFile;
    }

    private async Task<HashSet<string>> SharingHoldsAsync(CancellationToken cancellationToken)
        => (await sharingRepository.GetSnapshotAsync(cancellationToken)).Holds
            .Select(hold => hold.QueueItemId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// James's cleanup setting for a download Deluno has judged bad — the same
    /// "remove a bad download" / "delete a bad download's files" pair the
    /// setup guide already asks about. A release the client itself reported
    /// as failed only ever gets both done together, so both have to be on.
    /// </summary>
    private async Task<bool> ClientReportedFailureCleanupIsOnAsync(CancellationToken cancellationToken)
    {
        var settings = await platformRepository.GetAsync(cancellationToken);
        return settings.CleanupRemoveClientEntryAfterThreshold && settings.CleanupPurgePayloadAfterThreshold;
    }

    private async Task<string> ClearAsync(
        BlockedRelease release,
        HashSet<string> holds,
        bool clientReportedFailureCleanupOn,
        CancellationToken cancellationToken)
    {
        if (release.TorrentHashOrItemId is not { Length: > 0 } queueItemId ||
            release.DownloadClientId is not { Length: > 0 } clientId)
        {
            return RefusedDownloadCleanupOutcomes.NothingToClear;
        }

        // #882/#883/#885: unlike a file Deluno read for itself, clearing one
        // the client merely reported as failed is only ever automatic when
        // the user turned that on (#936). Left alone rather than marked
        // cleaned, so turning the setting on later still finds it.
        if (release.ReasonCode == ImportFailurePolicy.ClientReportedFailure && !clientReportedFailureCleanupOn)
        {
            return RefusedDownloadCleanupOutcomes.CleanupDisabled;
        }

        if (holds.Contains(queueItemId))
        {
            return RefusedDownloadCleanupOutcomes.StillSharing;
        }

        try
        {
            var result = await downloadClients.ExecuteActionAsync(
                clientId,
                new DownloadClientActionRequest(DownloadClientActions.Forget, queueItemId),
                cancellationToken);

            if (result.Refused)
            {
                // Deluno never sent this download (added by hand, or by another
                // tool), so it is not Deluno's to remove. Not the client's fault.
                return RefusedDownloadCleanupOutcomes.NotSentByDeluno;
            }

            if (!result.Succeeded)
            {
                return RefusedDownloadCleanupOutcomes.ClientUnavailable;
            }

            await blockedReleases.MarkCleanedUpAsync(release.Id, cancellationToken);

            // #882/#883/#885: this is an automatic action just like the
            // blocklist-and-search-again it follows, and it must be just as
            // visible — a logger line settles nothing for a person looking at
            // the screen, wondering whether the bad copy is actually gone.
            var entityType = string.Equals(release.MediaType, "tv", StringComparison.OrdinalIgnoreCase) ? "series" : "movie";
            var cleanupMessage = $"Deluno removed {release.ReleaseName} from {release.DownloadClientName ?? "its download client"} and deleted its files: {release.Reason}";
            await activityFeedRepository.RecordActivityAsync(
                "download.cleanup.cleared",
                cleanupMessage,
                null,
                null,
                entityType,
                release.EntityId,
                cancellationToken);
            var clearedTags = await mediaTagStore.ListTitleTagNamesAsync(entityType, release.EntityId, cancellationToken);
            await DispatchCleanupNotificationAsync(
                "download.cleanup.cleared",
                $"Cleanup cleared: {release.ReleaseName}",
                cleanupMessage,
                cancellationToken,
                clearedTags);

            return RefusedDownloadCleanupOutcomes.Cleared;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Left unmarked on purpose, so the next pass tries again. A client
            // that is off right now will not be off for ever.
            logger.LogWarning(exception, "Could not clear the blocked release {ReleaseName}.", release.ReleaseName);
            return RefusedDownloadCleanupOutcomes.ClientUnavailable;
        }
    }

    /// <summary>
    /// #966: cleanup-cleared already lands in Activity; this also sends it
    /// outbound, gated by its own preference and off by default (#936), the
    /// same shape as every other recovery/cleanup dispatch (#858).
    /// </summary>
    private async Task DispatchCleanupNotificationAsync(
        string eventCategory,
        string title,
        string message,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? titleTags = null)
    {
        if (outboundNotificationService is null)
        {
            return;
        }

        if (notificationPreferences is not null)
        {
            var preferences = await notificationPreferences.GetPreferencesAsync(cancellationToken);
            if (!preferences.CleanupClearedEnabled)
            {
                return;
            }
        }

        await outboundNotificationService.DispatchAsync(eventCategory, title, message, null, cancellationToken, titleTags);
    }
}
