using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;

namespace Deluno.Worker.Jobs;

/// <summary>
/// #875 (cleanup sheet B1): resends the exact same release — same download
/// URL, same indexer, same client — after the client was offline, refused
/// the password, or timed out. Runs on its own lane (scheduled by
/// <see cref="SearchExecutionSupport.RetryConnectivityFailureAsync"/>), the
/// way #920 gave media-server refresh retries their own lane, so a slow
/// client's retries never crowd out, or get crowded out by, the library's
/// ordinary search cycle.
///
/// <para>Never blocklists on its own — only a dead or refused release (#876)
/// does that, and that path is still honoured here if the client's answer
/// changes on retry. Bounded to
/// <see cref="SearchExecutionSupport.MaxConnectivityRetryAttempts"/> resends;
/// after that Deluno stops trying on its own but keeps the problem visible,
/// and the library's next scheduled search picks the title up again.</para>
/// </summary>
public sealed class DownloadClientRetryGrabJobHandler(
    IDownloadClientGrabService downloadClientGrabService,
    IJobQueueRepository jobQueueRepository,
    IActivityFeedRepository activityFeedRepository,
    IJobScheduler jobScheduler,
    TimeProvider timeProvider,
    Deluno.Libraries.Data.ILibrariesRepository? librariesRepository = null) : IJobHandler
{
    public string JobType => SearchExecutionSupport.DownloadClientRetryGrabJobType;

    public async Task<string> HandleAsync(JobQueueItem job, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(job.PayloadJson))
        {
            return "Connectivity retry skipped: nothing to resend.";
        }

        var payload = JsonSerializer.Deserialize<JobPayloads.DownloadClientRetryGrabPayload>(job.PayloadJson, JobPayloads.Options);
        if (payload is null)
        {
            return "Connectivity retry skipped: nothing to resend.";
        }

        var libraryName = payload.LibraryId;
        if (librariesRepository is not null)
        {
            try
            {
                libraryName = (await librariesRepository.ListLibrariesAsync(cancellationToken))
                    .FirstOrDefault(item => string.Equals(item.Id, payload.LibraryId, StringComparison.OrdinalIgnoreCase))?.Name ?? payload.LibraryId;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The id stands in for the name.
            }
        }

        var request = new DownloadClientGrabRequest(
            payload.ReleaseName,
            payload.DownloadUrl,
            payload.MediaType,
            payload.Category,
            payload.IndexerName,
            payload.DispatchId,
            payload.IndexerId)
        {
            Priority = payload.Priority,
            Password = payload.Password,
            TorrentFile = payload.TorrentFile,
            ReleaseHash = payload.TorrentHashOrItemId,
            OnDeadRelease = new DeadReleaseFollowUp(payload.EntityType, payload.EntityId, payload.Title, payload.LibraryId, libraryName)
        };

        var grabResult = await downloadClientGrabService.GrabAsync(payload.DownloadClientId, request, cancellationToken);

        await jobQueueRepository.RecordDownloadDispatchAsync(
            payload.LibraryId,
            payload.MediaType,
            payload.EntityType,
            payload.EntityId,
            payload.ReleaseName,
            payload.IndexerName ?? string.Empty,
            payload.DownloadClientId,
            payload.DownloadClientName,
            grabResult.Status,
            notesJson: null,
            grabResponseCode: grabResult.Succeeded ? 200 : 400,
            grabFailureCode: grabResult.Failure?.Code ?? grabResult.FailureCode,
            cancellationToken: cancellationToken,
            failure: grabResult.Failure,
            clientExternalId: grabResult.ExternalId);

        if (grabResult.Succeeded)
        {
            await activityFeedRepository.RecordActivityAsync(
                "download.client.connectivity-retry-succeeded",
                $"{payload.ReleaseName} was sent to {payload.DownloadClientName} on retry. The earlier setup problem has cleared.",
                null,
                null,
                payload.EntityType,
                payload.EntityId,
                cancellationToken);
            return $"{payload.ReleaseName} was sent to {payload.DownloadClientName} on retry.";
        }

        if (!SearchExecutionSupport.IsConnectivityOrSetupProblem(grabResult.Failure))
        {
            // The client answered with something else this time (for example
            // a dead release) — handled above; nothing left to resend.
            return $"{payload.ReleaseName} was not resent to {payload.DownloadClientName}: {grabResult.Message}";
        }

        if (payload.AttemptNumber >= SearchExecutionSupport.MaxConnectivityRetryAttempts)
        {
            await activityFeedRepository.RecordActivityAsync(
                "download.client.connectivity-retry-exhausted",
                $"{payload.ReleaseName} still could not be sent to {payload.DownloadClientName} after {payload.AttemptNumber} tries: {grabResult.Message} Deluno has stopped retrying this on its own; the next scheduled search will try again.",
                null,
                null,
                payload.EntityType,
                payload.EntityId,
                cancellationToken);
            return $"{payload.ReleaseName} still could not be sent to {payload.DownloadClientName}; giving up dedicated retries.";
        }

        await SearchExecutionSupport.ScheduleConnectivityRetryAsync(
            jobScheduler,
            timeProvider,
            payload.LibraryId,
            payload.MediaType,
            payload.EntityType,
            payload.EntityId,
            payload.Title,
            payload.DownloadClientId,
            payload.DownloadClientName,
            payload.ReleaseName,
            request,
            payload.AttemptNumber + 1,
            cancellationToken,
            payload.TorrentHashOrItemId,
            grabResult.FetchedTorrentFile);

        await activityFeedRepository.RecordActivityAsync(
            "download.client.connectivity-retry-scheduled",
            $"{payload.ReleaseName} still could not be sent to {payload.DownloadClientName}: {grabResult.Message} This is a setup problem, not the release — Deluno will try again shortly.",
            null,
            null,
            payload.EntityType,
            payload.EntityId,
            cancellationToken);

        return $"{payload.ReleaseName} still could not be sent to {payload.DownloadClientName}; another retry is scheduled.";
    }
}
