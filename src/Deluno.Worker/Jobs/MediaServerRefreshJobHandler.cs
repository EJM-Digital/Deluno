using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.MediaServers;
using Deluno.Jobs.Contracts;

namespace Deluno.Worker.Jobs;

/// <summary>
/// Tells every configured media server about changed files (#662), off the
/// import path entirely: a slow or down Plex/Jellyfin/Emby costs a queued
/// job, never a failed or delayed import.
/// </summary>
public sealed class MediaServerRefreshJobHandler(IMediaServerRefreshNotifier notifier) : IJobHandler
{
    public string JobType => MediaServerJobTypes.Refresh;

    public async Task<string> HandleAsync(JobQueueItem job, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(job.PayloadJson))
        {
            return "Media server refresh skipped because no changed files were given.";
        }

        var payload = JsonSerializer.Deserialize<MediaServerRefreshJobPayload>(job.PayloadJson);
        if (payload is null || payload.Changes.Count == 0)
        {
            return "Media server refresh skipped because no changed files were given.";
        }

        await notifier.NotifyAsync(payload.MediaType, payload.Changes, cancellationToken);
        return $"Told every configured media server about {payload.Changes.Count} changed file{(payload.Changes.Count == 1 ? string.Empty : "s")}.";
    }
}
