using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Microsoft.Extensions.Logging;

namespace Deluno.Jobs.Data;

/// <summary>
/// The one way a file-changing path tells the media servers (#662, #1365): it queues
/// the refresh job and never lets a failure to queue fail the change that already
/// happened on disk. Import, removal, a Files-tab delete, a rename, a move, a
/// restore from the recycle bin and a retention removal all go through here.
/// </summary>
public static class MediaServerTell
{
    public static async Task QueueAsync(
        IJobScheduler? scheduler,
        string mediaType,
        IReadOnlyList<MediaServerPathChange> changes,
        string source,
        string? relatedEntityType,
        string? relatedEntityId,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        if (scheduler is null || changes.Count == 0)
        {
            return;
        }

        try
        {
            await scheduler.EnqueueAsync(
                new EnqueueJobRequest(
                    MediaServerJobTypes.Refresh,
                    Source: source,
                    PayloadJson: JsonSerializer.Serialize(new MediaServerRefreshJobPayload(mediaType, changes)),
                    RelatedEntityType: relatedEntityType,
                    RelatedEntityId: relatedEntityId),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger?.LogWarning(exception, "Could not queue the media server refresh for {Source}.", source);
        }
    }
}
