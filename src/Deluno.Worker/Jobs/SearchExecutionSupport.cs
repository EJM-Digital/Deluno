using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Jobs.Data;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;

namespace Deluno.Worker.Jobs;

/// <summary>
/// Shared behaviour between <see cref="LibrarySearchJobHandler"/> and
/// <see cref="EpisodeSearchJobHandler"/> — grabbing a matched candidate,
/// serializing a search plan for storage, and resolving the custom formats
/// configured on a quality profile.
/// </summary>
internal static class SearchExecutionSupport
{
    /// <summary>How long a search whose indexers were all busy waits before it runs again (#598).</summary>
    internal static readonly TimeSpan IndexersBusyDelay = TimeSpan.FromMinutes(3);

    /// <summary>Whether no indexer was asked because each was still busy pacing other searches (#598).</summary>
    internal static bool IndexersWereBusy(MediaSearchPlan plan)
        => string.Equals(plan.Reason, MediaSearchReasons.IndexersBusy, StringComparison.Ordinal);

    /// <summary>
    /// A search that asked nobody is not a search (#598). Sixty episode searches
    /// queued together share each indexer's pace, and the ones that waited too
    /// long used to record "searched, nothing found" and wait a whole retry
    /// window. The job runs again shortly instead, with nothing recorded.
    /// </summary>
    internal static void DeferIfIndexersWereBusy(MediaSearchPlan plan)
    {
        if (IndexersWereBusy(plan))
        {
            throw new Deluno.Jobs.Contracts.JobDeferredException(IndexersBusyDelay, plan.Summary);
        }
    }

    internal static async Task<DownloadClientGrabResult> GrabBestCandidateAsync(
        IDownloadClientGrabService downloadClientGrabService,
        string downloadClientId,
        MediaSearchCandidate candidate,
        DownloadClientGrabRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(candidate.DownloadUrl))
        {
            return new DownloadClientGrabResult(
                downloadClientId,
                candidate.ReleaseName,
                false,
                "planned",
                "No download URL was available.")
            {
                Failure = IntegrationFailureFactory.FromLegacy(
                    "download-client",
                    downloadClientId,
                    downloadClientId,
                    "grab",
                    "planned",
                    "No downloadable URL was available for this release.")
            };
        }

        return await downloadClientGrabService.GrabAsync(
            downloadClientId,
            request,
            cancellationToken);
    }

    /// <summary>
    /// #876 (cleanup sheet B2): a grab the client refused outright — a dead
    /// link or a file it could not parse — is nobody's setup problem and
    /// nothing worth retrying unchanged. Blocklisting it here is what stops a
    /// later automatic search picking the exact same bad release and failing
    /// the same way again; the next search naturally tries a different one.
    ///
    /// <para>A no-op for any other grab outcome, including an ordinary
    /// connectivity failure (#875) — those are the client's or the network's
    /// fault, not the release's, and must never end up on the blocklist.</para>
    /// </summary>
    internal static async Task BlockDeadReleaseAsync(
        IBlockedReleaseRepository? blockedReleases,
        IActivityFeedRepository activityFeedRepository,
        DownloadClientGrabResult grabResult,
        string mediaType,
        string entityType,
        string entityId,
        string title,
        string indexerName,
        string clientName,
        CancellationToken cancellationToken)
    {
        if (blockedReleases is null || grabResult.Status != DownloadQueueStatuses.DeadRelease)
        {
            return;
        }

        await blockedReleases.BlockAsync(
            new BlockedRelease(
                Guid.NewGuid().ToString("n"),
                BlockedReleaseKeys.For(grabResult.ReleaseName, indexerName),
                grabResult.ReleaseName,
                indexerName,
                mediaType,
                entityId,
                title,
                ImportFailurePolicy.DeadOrBrokenLink,
                grabResult.Message,
                null,
                null,
                null,
                DateTimeOffset.MinValue),
            cancellationToken);

        await activityFeedRepository.RecordActivityAsync(
            "download.release.dead-link",
            $"{grabResult.ReleaseName} could not be sent to {clientName}: {grabResult.Message} Deluno blocklisted it and will pick a different release next search.",
            null,
            null,
            entityType,
            entityId,
            cancellationToken);
    }

    /// <summary>
    /// Why a release the search chose is not sent, when this title or that exact
    /// release already has a download on its way; null when nothing is (#555).
    ///
    /// <para>The downloading mark keeps a movie or a series off the work list
    /// once a grab is sent. This covers what the mark cannot: an episode, which
    /// has no such mark; the series and episode passes reaching the same release
    /// from their two sides; and titles grabbed before automatic grabs set the
    /// mark at all.</para>
    /// </summary>
    internal static Task<string?> AlreadyOnItsWayAsync(
        IJobQueueRepository jobQueueRepository,
        string releaseName,
        IReadOnlyCollection<string> entityIds,
        CancellationToken cancellationToken)
        => DispatchCoverage.AlreadyOnItsWayAsync(jobQueueRepository, releaseName, entityIds, cancellationToken);

    internal static string? SerializeSearchPlan(MediaSearchPlan plan, DownloadClientGrabResult? grabResult = null)
    {
        if (plan.Candidates.Count == 0)
        {
            return null;
        }

        return grabResult is null
            ? JsonSerializer.Serialize(plan, JobPayloads.Options)
            : JsonSerializer.Serialize(new { searchPlan = plan, grabResult }, JobPayloads.Options);
    }

    internal static string SerializeCycleNotes(
        int configuredSources,
        int configuredClients,
        int checkedCount,
        int matchedCount,
        int blockedCount,
        int heldCount,
        int retryDelayedCount,
        int maxItems,
        int apiCallCount,
        long queuedReleaseBytes)
    {
        return JsonSerializer.Serialize(new
        {
            configuredSources,
            configuredClients,
            checkedCount,
            matchedCount,
            blockedCount,
            heldCount,
            retryDelayedCount,
            maxItems,
            apiCallCount,
            queuedReleaseBytes
        }, JobPayloads.Options);
    }

    internal static string NormalizeActionKind(string? wantedStatus)
        => string.Equals(wantedStatus, "upgrade", StringComparison.OrdinalIgnoreCase)
            ? "upgrade"
            : "missing";

    internal static async Task<IReadOnlyList<CustomFormatItem>> ResolveCustomFormatsAsync(
        IQualityRepository repository,
        string? qualityProfileId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(qualityProfileId))
        {
            return [];
        }

        var profiles = await repository.ListQualityProfilesAsync(cancellationToken);
        var profile = profiles.FirstOrDefault(item => item.Id == qualityProfileId);
        if (profile is null || string.IsNullOrWhiteSpace(profile.CustomFormatIds))
        {
            return [];
        }

        var ids = profile.CustomFormatIds
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (ids.Length == 0)
        {
            return [];
        }

        var formats = await repository.ListCustomFormatsAsync(cancellationToken);
        return formats.Where(item => ids.Contains(item.Id, StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    internal static string FormatExecutionMessage(
        string libraryName,
        int candidateCount,
        int sourceCount,
        int clientCount,
        string mediaLabel)
    {
        if (candidateCount == 0)
        {
            return $"Deluno checked {libraryName} and found nothing else to look for right now.";
        }

        if (sourceCount == 0)
        {
            return $"Deluno found {candidateCount} {mediaLabel}{(candidateCount == 1 ? "" : "s")} to search in {libraryName}, but this library does not have any indexers linked yet.";
        }

        if (clientCount == 0)
        {
            return $"Deluno found {candidateCount} {mediaLabel}{(candidateCount == 1 ? "" : "s")} to search in {libraryName}, but it still needs a download client for this library.";
        }

        return $"Deluno checked {candidateCount} {mediaLabel}{(candidateCount == 1 ? "" : "s")} in {libraryName} using {sourceCount} source{(sourceCount == 1 ? "" : "s")}.";
    }

    internal static string FormatCompletionMessage(
        string libraryName,
        int candidateCount,
        int sourceCount,
        int clientCount,
        string mediaLabel)
    {
        if (candidateCount == 0)
        {
            return $"Finished checking {libraryName}. Nothing else needs attention right now.";
        }

        if (sourceCount == 0)
        {
            return $"Finished checking {libraryName}. Deluno found {candidateCount} {mediaLabel}{(candidateCount == 1 ? "" : "s")} but this library still needs indexers.";
        }

        if (clientCount == 0)
        {
            return $"Finished checking {libraryName}. Deluno found {candidateCount} {mediaLabel}{(candidateCount == 1 ? "" : "s")} but this library still needs a download client.";
        }

        return $"Finished checking {libraryName}. Deluno reviewed {candidateCount} {mediaLabel}{(candidateCount == 1 ? "" : "s")} for new or better releases.";
    }
}
