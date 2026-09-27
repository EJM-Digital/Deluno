using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.MediaServers;
using Deluno.Jobs.Contracts;

namespace Deluno.Worker.Jobs;

/// <summary>
/// One server and media type's own retry (#920): the job
/// <see cref="MediaServerRefreshNotifier"/> schedules with backoff whenever a
/// refresh fails, until the server answers. A failure here re-throws so the
/// job queue's own retry/backoff takes over the "try again in a minute"
/// scheduling would otherwise duplicate - <see cref="IJobHandler"/> jobs that
/// throw are retried by the queue itself, and the notifier already scheduled
/// the *next* backoff step as this job's own re-enqueue before returning
/// false, so either mechanism landing the retry is fine; what matters is that
/// nothing here ever touches, undoes or delays the import that already ran.
/// </summary>
public sealed class MediaServerRefreshRetryJobHandler(IMediaServerRefreshNotifier notifier) : IJobHandler
{
    public string JobType => MediaServerJobTypes.RefreshRetry;

    public async Task<string> HandleAsync(JobQueueItem job, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(job.PayloadJson))
        {
            return "Media server refresh retry skipped: no server was named.";
        }

        var payload = JsonSerializer.Deserialize<MediaServerRefreshRetryJobPayload>(job.PayloadJson);
        if (payload is null)
        {
            return "Media server refresh retry skipped: no server was named.";
        }

        var answered = await notifier.RetryAsync(payload.MediaServerId, payload.MediaType, cancellationToken);
        return answered
            ? "The server answered; anything it missed was sent."
            : "The server still is not answering; another retry is scheduled.";
    }
}
