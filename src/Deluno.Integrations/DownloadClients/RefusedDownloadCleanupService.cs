using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Notifications;
using Deluno.Platform.Contracts;
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
/// <para><b>It asks the client to remove the refused copy, not just to delete.</b> On a torrent
/// client those are one request; on SABnzbd and NZBGet, removing the refused copy also clears
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
    IMediaTagStore? mediaTagStore = null,
    // The leftover payload sweep (#1167). Without them there are no folders to look in and it does nothing.
    Deluno.Libraries.Data.ILibrariesRepository? librariesRepository = null,
    IJobQueueRepository? jobQueueRepository = null,
    IProcessorRepository? processorRepository = null,
    TimeProvider? timeProvider = null,
    // Each client's own incomplete folder, as the folder check reads it (#1167): a payload a refused release left
    // there is on a drive no library or listed download is on.
    IFolderChainService? folderChain = null) : IRefusedDownloadCleanupService
{
    public async Task<RefusedDownloadCleanupResult> CleanUpEverythingAsync(CancellationToken cancellationToken)
    {
        var pending = await blockedReleases.ListAwaitingCleanupAsync(await ShouldDeletePayloadAsync(cancellationToken), cancellationToken);
        // Whatever the clients forgot without their files is swept too (#1167), pending refusals or none.
        var swept = await SweepLeftoverPayloadsAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return new RefusedDownloadCleanupResult(swept, 0, 0);
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

        return new RefusedDownloadCleanupResult(cleared + swept, waiting, failed);
    }

    /// <summary>
    /// A refused release whose client entry was removed without its files (an older build, or a client that kept
    /// them) left its payload on disk with nothing listing it (#1167). Deletes that payload, with an Activity line,
    /// when it sits under one of Deluno's own category or incomplete folders, belongs to a release Deluno sent and
    /// refused, and no client, import, hand-off or library has it. A payload that carries an executable goes whatever
    /// the refusal's reason. Returns how many it deleted.
    /// </summary>
    private async Task<int> SweepLeftoverPayloadsAsync(CancellationToken cancellationToken)
    {
        if (librariesRepository is null)
        {
            return 0;
        }

        try
        {
            var refused = (await blockedReleases.ListAsync(cancellationToken))
                .Where(release => release.State == BlockedReleaseStates.Refused)
                .ToArray();
            if (refused.Length == 0)
            {
                return 0;
            }

            var settings = await platformRepository.GetAsync(cancellationToken);
            var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);

            // #1325: no clean-up while a folder overlaps a library; the problem is on System, Health.
            if (Deluno.Libraries.LibraryFolderLayout.CleanupBlockedReason(libraries) is not null)
            {
                return 0;
            }

            var snapshots = await downloadClients.GetSnapshotsForLeftoverScanAsync(cancellationToken);

            var listedPaths = new List<string>();
            var listedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var client in snapshots)
            {
                foreach (var item in client.Queue)
                {
                    listedPaths.Add(item.SourcePath ?? string.Empty);
                    listedNames.Add(item.ReleaseName);
                }

                foreach (var item in client.History)
                {
                    listedPaths.Add(item.SourcePath ?? string.Empty);
                    listedNames.Add(item.ReleaseName);
                }

                listedPaths.AddRange(client.OutsideSourcePaths);
            }

            var claimed = new List<string>();
            if (jobQueueRepository is not null)
            {
                claimed.AddRange((await jobQueueRepository.ListAsync(600, cancellationToken))
                    .Where(job => string.Equals(job.JobType, "filesystem.import.execute", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(job.Status, "dead-letter", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(job.Status, "completed", StringComparison.OrdinalIgnoreCase))
                    .Select(job => DownloadClientTelemetryService.TryReadImportSourcePath(job.PayloadJson) ?? string.Empty));
            }

            if (processorRepository is not null)
            {
                claimed.AddRange((await processorRepository.ListProcessorHandoffsWithStatusAsync(ProcessorHandoffStatuses.WithProcessor, cancellationToken))
                    .SelectMany(handoff => new[] { handoff.SourcePath, handoff.OutputPath ?? string.Empty }));
            }

            var roots = new List<string>();
            if (!string.IsNullOrWhiteSpace(settings.IncompleteDownloadsPath))
            {
                roots.Add(settings.IncompleteDownloadsPath);
            }

            roots.AddRange(libraries.Select(library => library.DownloadsPath ?? string.Empty));
            roots.AddRange(OrphanedDownloadService.DownloadFolders(libraries, snapshots).Select(folder => folder.Path));
            // The client's own incomplete or temporary folder, with its category folders below it: qBittorrent's
            // temp path, Deluge's download location, Transmission's incomplete folder, SABnzbd's and NZBGet's
            // temporary and intermediate folders. Only what a release Deluno sent and refused is named there.
            roots.AddRange(await ClientIncompleteFoldersAsync(libraries, cancellationToken));

            var cleanupOn = await ClientReportedFailureCleanupIsOnAsync(cancellationToken);
            var deletable = await ShouldDeletePayloadAsync(cancellationToken);
            var payloads = RefusedPayloadSweep.Find(
                roots.Where(root => !string.IsNullOrWhiteSpace(root)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                refused.Where(release =>
                {
                    if (!listedNames.Contains(release.ReleaseName))
                    {
                        return true;
                    }

                    logger.LogDebug("Leftover sweep: {ReleaseName} is not swept because a download client still lists a download of that name.", release.ReleaseName);
                    return false;
                }).ToArray(),
                listedPaths,
                libraries.Select(library => library.RootPath).ToArray(),
                claimed,
                release => deletable(release.ReasonCode)
                    || (release.ReasonCode == ImportFailurePolicy.ClientReportedFailure && cleanupOn),
                (timeProvider ?? TimeProvider.System).GetUtcNow(),
                OrphanedDownloads.Grace,
                (path, because) => logger.LogDebug("Leftover sweep: {Path} is not deleted because {Because}.", path, because));

            var deleted = 0;
            foreach (var payload in payloads)
            {
                if (Deluno.Libraries.LibraryFolderLayout.ReachesALibrary(libraries, payload.Path))
                {
                    continue;
                }

                try
                {
                    if (payload.IsDirectory)
                    {
                        Directory.Delete(payload.Path, recursive: true);
                    }
                    else
                    {
                        File.Delete(payload.Path);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(exception, "Could not delete the leftover files of the refused release {ReleaseName}.", payload.Release.ReleaseName);
                    continue;
                }

                deleted++;
                await activityFeedRepository.RecordActivityAsync(
                    "download.cleanup.cleared",
                    payload.CarriesExecutable
                        ? $"Deluno deleted the leftover files of {payload.Release.ReleaseName} ({FormatSize(payload.SizeBytes)}). The release was refused, no download client lists it any more, and it carries an executable."
                        : $"Deluno deleted the leftover files of {payload.Release.ReleaseName} ({FormatSize(payload.SizeBytes)}). The release was refused ({payload.Release.Reason}) and no download client lists it any more.",
                    System.Text.Json.JsonSerializer.Serialize(new { payload.Path, payload.Release.ReleaseName, payload.SizeBytes, payload.CarriesExecutable }),
                    null,
                    string.Equals(payload.Release.MediaType, "tv", StringComparison.OrdinalIgnoreCase) ? "series" : "movie",
                    payload.Release.EntityId,
                    cancellationToken);
            }

            return deleted;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A sweep that cannot read a folder or a client is tried again next pass; it never stops the cleanup.
            logger.LogWarning(exception, "The leftover-payload sweep for refused releases failed.");
            return 0;
        }
    }

    private async Task<IReadOnlyList<string>> ClientIncompleteFoldersAsync(
        IReadOnlyList<Deluno.Libraries.Contracts.LibraryItem> libraries,
        CancellationToken cancellationToken)
    {
        if (folderChain is null)
        {
            return [];
        }

        var folders = new List<string>();
        foreach (var library in libraries)
        {
            try
            {
                var report = await folderChain.CheckAsync(library.Id, cancellationToken);
                folders.AddRange((report?.Clients ?? [])
                    .Select(client => client.IncompleteFolder)
                    .Where(folder => !string.IsNullOrWhiteSpace(folder))!);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A client that cannot be read now is read again next pass; the other roots are still swept.
                logger.LogWarning(exception, "Could not read the incomplete folders of the download clients for {Library}.", library.Name);
            }
        }

        return folders;
    }

    private static string FormatSize(long bytes)
        => bytes >= 1_073_741_824 ? $"{bytes / 1_073_741_824d:0.0} GB" : $"{bytes / 1_048_576d:0} MB";
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
            var removeCopy = new DownloadClientActionRequest(DownloadClientActions.RemoveRefusedCopy, queueItemId);
            var result = await downloadClients.ExecuteActionAsync(clientId, removeCopy, cancellationToken);

            if (result.Refused)
            {
                // Deluno never sent this download (added by hand, or by another
                // tool), so it is not Deluno's to remove. That is settled, not
                // "awaiting cleanup" for ever: the row is closed and the
                // Activity feed says why nothing was removed.
                await blockedReleases.MarkCleanedUpAsync(release.Id, cancellationToken);
                await activityFeedRepository.RecordActivityAsync(
                    "download.cleanup.left-alone",
                    $"{release.ReleaseName} is still in {release.DownloadClientName ?? "its download client"}. Deluno did not send that download, so it left it there. The release stays blocked.",
                    null,
                    null,
                    string.Equals(release.MediaType, "tv", StringComparison.OrdinalIgnoreCase) ? "series" : "movie",
                    release.EntityId,
                    cancellationToken);
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
