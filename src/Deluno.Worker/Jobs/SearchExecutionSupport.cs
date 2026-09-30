using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Jobs.Contracts;
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
    /// #877 (cleanup sheet B3): a torrent client saying it already holds this
    /// release is nobody's fault, and Deluno follows the copy it has instead
    /// of sending another — but that is still an automatic decision, so it is
    /// written to Activity in plain words rather than looking, from the
    /// dispatch's ordinary "sent" record alone, exactly like every other
    /// grab.
    /// </summary>
    internal static async Task RecordDuplicateFollowAsync(
        IActivityFeedRepository activityFeedRepository,
        DownloadClientGrabResult grabResult,
        string entityType,
        string entityId,
        CancellationToken cancellationToken)
    {
        if (!grabResult.IsDuplicateFollow)
        {
            return;
        }

        await activityFeedRepository.RecordActivityAsync(
            "download.grab.duplicate",
            grabResult.Message,
            null,
            null,
            entityType,
            entityId,
            cancellationToken);
    }

    /// <summary>
    /// #808: the same plain-English reason a manual grab explains in Activity
    /// (a title's tags sent it to this client rather than another one routed
    /// to the same library), carried into automatic search too. A no-op for
    /// the ordinary case of an untagged client or a failed grab — an
    /// automatic grab already writes its own record on failure through
    /// the grab service's own dead-release step or
    /// <see cref="RetryConnectivityFailureAsync"/>, and there is nothing
    /// about tags to explain when they played no part in the choice.
    /// </summary>
    internal static async Task RecordAutomaticGrabRoutingAsync(
        IActivityFeedRepository activityFeedRepository,
        IAcquisitionDecisionPipeline acquisitionPipeline,
        DownloadClientGrabResult grabResult,
        string downloadClientId,
        string downloadClientName,
        IReadOnlyList<string>? titleTags,
        string entityType,
        string entityId,
        CancellationToken cancellationToken)
    {
        if (!grabResult.Succeeded)
        {
            return;
        }

        var routingReason = await acquisitionPipeline.ExplainDownloadClientRoutingAsync(
            downloadClientId,
            titleTags,
            cancellationToken);
        if (routingReason is null)
        {
            return;
        }

        await activityFeedRepository.RecordActivityAsync(
            $"{entityType}.release.grabbed",
            $"{grabResult.ReleaseName} was automatically sent to {downloadClientName}. {routingReason}",
            null,
            null,
            entityType,
            entityId,
            cancellationToken);
    }

    /// <summary>How long Deluno waits before automatically trying the same release again after a connectivity or setup problem (#875).</summary>
    internal static readonly TimeSpan ConnectivityRetryDelay = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How many times Deluno resends the exact same release before it stops
    /// dedicated retries and leaves the title to the library's own scheduled
    /// search (#875). Eight tries at fifteen minutes apart is two hours of a
    /// client having every chance to come back; a client down longer than
    /// that needs a person to look at it, not a job retrying forever.
    /// </summary>
    internal const int MaxConnectivityRetryAttempts = 8;

    /// <summary>
    /// Whether an <see cref="IntegrationFailure"/> says something about
    /// reaching the client — offline, wrong password, timed out — rather than
    /// about the release itself (#875, #880, #901).
    /// </summary>
    internal static bool IsConnectivityOrSetupProblem(IntegrationFailure? failure)
        => failure is { } value && value.Kind is IntegrationFailureKind.Timeout
            or IntegrationFailureKind.Unavailable
            or IntegrationFailureKind.Authentication
            or IntegrationFailureKind.CircuitOpen;

    /// <summary>
    /// #875 (cleanup sheet B1): the download client was offline, refused the
    /// password, or timed out — Deluno's own setup, not the release's fault
    /// (#880, #901). Nothing here is ever blocklisted; instead the exact same
    /// release is resent to the exact same client once the client has had a
    /// chance to come back, on its own retry lane (#920's pattern) rather
    /// than waiting for whatever the next ordinary search cycle happens to
    /// be, and the problem is written to Activity so it stays visible until
    /// it clears.
    ///
    /// <para>A no-op for a dead release (#876) or an ordinary success — this
    /// only fires for a failure that says something about reaching the
    /// client, never about the release itself.</para>
    /// </summary>
    internal static async Task RetryConnectivityFailureAsync(
        IJobScheduler? jobScheduler,
        IActivityFeedRepository activityFeedRepository,
        DownloadClientGrabResult grabResult,
        DownloadClientGrabRequest request,
        TimeProvider timeProvider,
        string libraryId,
        string mediaType,
        string entityType,
        string entityId,
        string title,
        string downloadClientId,
        string clientName,
        CancellationToken cancellationToken,
        // #874/#942: carried into the retry payload so a resend that turns
        // out to be a dead release blocklists it with its hash/NZB id, the
        // same as the first attempt would have.
        string? torrentHashOrItemId = null)
    {
        if (jobScheduler is null || grabResult.Succeeded || !IsConnectivityOrSetupProblem(grabResult.Failure))
        {
            return;
        }

        await ScheduleConnectivityRetryAsync(
            jobScheduler,
            timeProvider,
            libraryId,
            mediaType,
            entityType,
            entityId,
            title,
            downloadClientId,
            clientName,
            grabResult.ReleaseName,
            request,
            attemptNumber: 1,
            cancellationToken,
            torrentHashOrItemId,
            grabResult.FetchedTorrentFile);

        await activityFeedRepository.RecordActivityAsync(
            "download.client.connectivity-retry-scheduled",
            $"{grabResult.ReleaseName} could not be sent to {clientName}: {grabResult.Message} This is a setup problem, not the release — Deluno will try sending the same release to {clientName} again shortly.",
            null,
            null,
            entityType,
            entityId,
            cancellationToken);
    }

    /// <summary>
    /// Schedules one more attempt to send <paramref name="request"/> — the
    /// exact same release, same download URL and indexer — to the same
    /// client, on the dedicated retry lane so it never crowds out an
    /// ordinary search cycle (#875, following #920's media-server lane).
    /// </summary>
    internal static async Task ScheduleConnectivityRetryAsync(
        IJobScheduler jobScheduler,
        TimeProvider timeProvider,
        string libraryId,
        string mediaType,
        string entityType,
        string entityId,
        string title,
        string downloadClientId,
        string clientName,
        string releaseName,
        DownloadClientGrabRequest request,
        int attemptNumber,
        CancellationToken cancellationToken,
        string? torrentHashOrItemId = null,
        byte[]? carriedTorrentFile = null)
    {
        var retryAt = timeProvider.GetUtcNow() + ConnectivityRetryDelay;
        var payload = new JobPayloads.DownloadClientRetryGrabPayload(
            downloadClientId,
            clientName,
            libraryId,
            mediaType,
            entityType,
            entityId,
            title,
            releaseName,
            request.DownloadUrl,
            request.Category,
            request.IndexerName,
            request.IndexerId,
            request.DispatchId,
            request.Priority,
            request.Password,
            request.TorrentFile ?? carriedTorrentFile,
            attemptNumber,
            torrentHashOrItemId);

        await jobScheduler.EnqueueAsync(
            new EnqueueJobRequest(
                DownloadClientRetryGrabJobType,
                "download-client-connectivity-retry",
                JsonSerializer.Serialize(payload, JobPayloads.Options),
                entityType,
                entityId,
                ScheduledUtc: retryAt,
                IdempotencyKey: $"download-client-retry-grab:{entityType}:{entityId}:{attemptNumber}",
                DedupeKey: $"download-client-retry-grab:{entityType}:{entityId}"),
            cancellationToken);
    }

    /// <summary>
    /// The job type a connectivity retry (#875) runs under: its own lane, so
    /// a client that is slow to come back never delays or is delayed by the
    /// library's ordinary search cycle.
    /// </summary>
    internal const string DownloadClientRetryGrabJobType = "download-client.retry-grab";

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
