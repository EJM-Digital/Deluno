using Deluno.Contracts;
using Deluno.Jobs.Data;
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
/// </summary>
public sealed class RefusedDownloadCleanupService(
    IBlockedReleaseRepository blockedReleases,
    IDownloadClientTelemetryService downloadClients,
    IDownloadSharingRepository sharingRepository,
    ILogger<RefusedDownloadCleanupService> logger) : IRefusedDownloadCleanupService
{
    public async Task<RefusedDownloadCleanupResult> CleanUpEverythingAsync(CancellationToken cancellationToken)
    {
        var pending = await blockedReleases.ListAwaitingCleanupAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return new RefusedDownloadCleanupResult(0, 0, 0);
        }

        var holds = await SharingHoldsAsync(cancellationToken);
        var cleared = 0;
        var waiting = 0;
        var failed = 0;

        foreach (var release in pending)
        {
            switch (await ClearAsync(release, holds, cancellationToken))
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
        var release = (await blockedReleases.ListAwaitingCleanupAsync(cancellationToken))
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

        return await ClearAsync(release, await SharingHoldsAsync(cancellationToken), cancellationToken);
    }

    private async Task<HashSet<string>> SharingHoldsAsync(CancellationToken cancellationToken)
        => (await sharingRepository.GetSnapshotAsync(cancellationToken)).Holds
            .Select(hold => hold.QueueItemId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private async Task<string> ClearAsync(
        BlockedRelease release,
        HashSet<string> holds,
        CancellationToken cancellationToken)
    {
        if (release.TorrentHashOrItemId is not { Length: > 0 } queueItemId ||
            release.DownloadClientId is not { Length: > 0 } clientId)
        {
            return RefusedDownloadCleanupOutcomes.NothingToClear;
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

            if (!result.Succeeded)
            {
                return RefusedDownloadCleanupOutcomes.ClientUnavailable;
            }

            await blockedReleases.MarkCleanedUpAsync(release.Id, cancellationToken);
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
}
