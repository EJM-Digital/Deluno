using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Movies.Data;
using Deluno.Notifications;
using Deluno.Platform;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Recovery.Services;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging;

namespace Deluno.Filesystem;

public interface IUnmatchedImportGiveUpService
{
    /// <summary>
    /// #888: when the "give up after N days" policy is chosen, finds every
    /// unmatched import recovery case nobody has acted on past that limit and
    /// gives up on it - removes the download, deletes the file, blocklists
    /// the release, and searches again. A no-op under any other policy, so a
    /// setup gap or an outage that merely looks unmatched is never touched
    /// unless the user chose this specifically.
    /// </summary>
    Task<int> RunAsync(CancellationToken cancellationToken);
}

public sealed class UnmatchedImportGiveUpService(
    IPlatformSettingsRepository platformRepository,
    IMovieCatalogRepository movieCatalogRepository,
    ISeriesCatalogRepository seriesCatalogRepository,
    IDownloadDispatchesRepository? downloadDispatchesRepository,
    IBlockedReleaseRepository? blockedReleases,
    IDownloadClientActionGateway? downloadClientActions,
    IActivityFeedRepository activityFeedRepository,
    TimeProvider timeProvider,
    ILogger<UnmatchedImportGiveUpService> logger,
    // Both optional so an install that never wired a job scheduler here
    // (or a test that does not need one) still gives up on files and
    // blocklists releases as before -- only the search-for-another step
    // (#888) is skipped when either is missing.
    IJobScheduler? jobScheduler = null,
    ILibrariesRepository? librariesRepository = null,
    IOutboundNotificationService? outboundNotificationService = null,
    INotificationService? notificationPreferences = null,
    // #1188: a file inside a library goes to the recycle bin, never off the disk.
    Deluno.Platform.IRecycleBinService? recycleBinService = null)
    : IUnmatchedImportGiveUpService
{
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var settings = await platformRepository.GetAsync(cancellationToken);
        if (settings.UnmatchedImportPolicy != UnmatchedImportPolicies.GiveUpAfterDays)
        {
            return 0;
        }

        var cutoff = timeProvider.GetUtcNow() - TimeSpan.FromDays(Math.Max(1, settings.UnmatchedImportGiveUpDays));
        var handled = 0;

        var movies = await movieCatalogRepository.GetImportRecoverySummaryAsync(cancellationToken);
        foreach (var recoveryCase in movies.RecentCases.Where(item => IsOverdueUnmatchedCase(item.FailureKind, item.DetectedUtc, cutoff)))
        {
            if (await GiveUpAsync(recoveryCase.Id, recoveryCase.Title, recoveryCase.DetailsJson, "movie", cancellationToken))
            {
                await movieCatalogRepository.ResolveImportRecoveryCaseAsync(recoveryCase.Id, "resolved", cancellationToken);
                handled++;
            }
        }

        var series = await seriesCatalogRepository.GetImportRecoverySummaryAsync(cancellationToken);
        foreach (var recoveryCase in series.RecentCases.Where(item => IsOverdueUnmatchedCase(item.FailureKind, item.DetectedUtc, cutoff)))
        {
            if (await GiveUpAsync(recoveryCase.Id, recoveryCase.Title, recoveryCase.DetailsJson, "series", cancellationToken))
            {
                await seriesCatalogRepository.ResolveImportRecoveryCaseAsync(recoveryCase.Id, "resolved", cancellationToken);
                handled++;
            }
        }

        return handled;
    }

    private static bool IsOverdueUnmatchedCase(string failureKind, DateTimeOffset detectedUtc, DateTimeOffset cutoff)
        => failureKind == ImportFailurePolicy.Unmatched && detectedUtc <= cutoff;

    private async Task<bool> GiveUpAsync(string caseId, string title, string? detailsJson, string entityKind, CancellationToken cancellationToken)
    {
        // #1325: no clean-up while a folder overlaps a library; the case stays open and is tried again once the layout is fixed.
        if (librariesRepository is not null
            && Deluno.Libraries.LibraryFolderLayout.CleanupBlockedReason(await librariesRepository.ListLibrariesAsync(cancellationToken)) is not null)
        {
            return false;
        }

        var (dispatchId, sourcePath) = ParseDetails(detailsJson);
        var deletedFile = false;
        var recycledFile = false;
        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            try
            {
                if (File.Exists(sourcePath))
                {
                    // #1188: an untracked file the library check found INSIDE a library is a
                    // person's media until they say otherwise. It leaves through the recycle
                    // bin or not at all. Only a file in a download area is deleted outright.
                    var owner = librariesRepository is null
                        ? null
                        : (await librariesRepository.ListLibrariesAsync(cancellationToken))
                            .FirstOrDefault(item => IsInsideLibrary(sourcePath, item.RootPath));
                    if (owner is null)
                    {
                        File.Delete(sourcePath);
                        deletedFile = true;
                    }
                    else if (recycleBinService is not null
                             && await recycleBinService.StoreRemovedFileAsync(
                                 owner,
                                 sourcePath,
                                 null,
                                 cancellationToken) is not null)
                    {
                        recycledFile = true;
                    }
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not delete the unmatched file for {Title} while giving up on it.", title);
            }
        }

        var removedFromClient = false;
        var blocklisted = false;
        var searchStarted = false;
        if (!string.IsNullOrWhiteSpace(dispatchId) && downloadDispatchesRepository is not null)
        {
            var dispatch = await downloadDispatchesRepository.GetDispatchAsync(dispatchId, cancellationToken);
            if (dispatch is not null)
            {
                if (downloadClientActions is not null &&
                    !string.IsNullOrWhiteSpace(dispatch.DownloadClientId) &&
                    !string.IsNullOrWhiteSpace(dispatch.TorrentHashOrItemId))
                {
                    var removal = await downloadClientActions.RemoveWithDataAsync(
                        dispatch.DownloadClientId,
                        dispatch.TorrentHashOrItemId,
                        cancellationToken);
                    removedFromClient = removal.Succeeded;
                }

                if (blockedReleases is not null && !string.IsNullOrWhiteSpace(dispatch.ReleaseName))
                {
                    await blockedReleases.BlockAsync(
                        new BlockedRelease(
                            Guid.NewGuid().ToString("n"),
                            BlockedReleaseKeys.For(dispatch.ReleaseName, dispatch.IndexerName),
                            dispatch.ReleaseName,
                            dispatch.IndexerName,
                            dispatch.MediaType,
                            dispatch.EntityId,
                            title,
                            ImportFailurePolicy.Unmatched,
                            $"Gave up waiting for you to match {title} by hand.",
                            dispatch.TorrentHashOrItemId,
                            dispatch.DownloadClientId,
                            dispatch.DownloadClientName,
                            DateTimeOffset.MinValue,
                            BlockedReleaseStates.Refused),
                        cancellationToken);
                    blocklisted = true;
                }

                // #888: the dispatch this download was for names the title
                // that wanted it, so a search is queued for exactly that
                // title (or, for a season pack, exactly the episodes it
                // covered) rather than left to the library's own next
                // missing/upgrade cycle -- which is only the fallback for a
                // give-up with no dispatch link to point at.
                if (jobScheduler is not null && ReplacementSearchQueuer.Supports(dispatch.EntityType))
                {
                    var library = librariesRepository is null
                        ? null
                        : (await librariesRepository.ListLibrariesAsync(cancellationToken))
                            .FirstOrDefault(item => string.Equals(item.Id, dispatch.LibraryId, StringComparison.OrdinalIgnoreCase));
                    await ReplacementSearchQueuer.EnqueueAsync(jobScheduler, dispatch, library?.Name ?? dispatch.LibraryId, cancellationToken);
                    searchStarted = true;
                }
            }
        }

        var summary = string.Join(", ", new[]
        {
            deletedFile ? "deleted the file" : null,
            recycledFile ? "moved the file to the recycle bin" : null,
            removedFromClient ? "removed it from the download client" : null,
            blocklisted ? "blocklisted the release" : null,
            searchStarted ? "started a new search" : null
        }.Where(part => part is not null));

        var giveUpMessage = summary.Length > 0
            ? searchStarted
                ? $"Gave up waiting to match {title} by hand: {summary}."
                : $"Gave up waiting to match {title} by hand: {summary}. A new search will run on its usual schedule."
            : $"Gave up waiting to match {title} by hand, but nothing was left to clean up.";
        var giveUpDetails = JsonSerializer.Serialize(new { CaseId = caseId, Title = title, DeletedFile = deletedFile, RecycledFile = recycledFile, RemovedFromClient = removedFromClient, Blocklisted = blocklisted, SearchStarted = searchStarted });
        await activityFeedRepository.RecordActivityAsync(
            "filesystem.import.unmatched-given-up",
            giveUpMessage,
            giveUpDetails,
            null,
            entityKind,
            null,
            cancellationToken);
        // #966: gated by its own preference, off by default (#936), the
        // same shape as every other recovery/cleanup dispatch (#858).
        if (outboundNotificationService is not null)
        {
            var enabled = notificationPreferences is null ||
                (await notificationPreferences.GetPreferencesAsync(cancellationToken)).UnmatchedImportGivenUpEnabled;
            if (enabled)
            {
                await outboundNotificationService.DispatchAsync(
                    "filesystem.import.unmatched-given-up",
                    $"Gave up waiting to match: {title}",
                    giveUpMessage,
                    giveUpDetails,
                    cancellationToken);
            }
        }

        return true;
    }

    private static (string? DispatchId, string? SourcePath) ParseDetails(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(detailsJson);
            var dispatchId = document.RootElement.TryGetProperty("DispatchId", out var dispatchElement) && dispatchElement.ValueKind == JsonValueKind.String
                ? dispatchElement.GetString()
                : null;
            var sourcePath = document.RootElement.TryGetProperty("SourcePath", out var sourceElement) && sourceElement.ValueKind == JsonValueKind.String
                ? sourceElement.GetString()
                : null;
            return (dispatchId, sourcePath);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static bool IsInsideLibrary(string path, string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        try
        {
            return Deluno.Contracts.SafePath.IsInside(Path.GetFullPath(root), Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A path that cannot be read is treated as inside: it is then never deleted outright.
            return true;
        }
    }
}
