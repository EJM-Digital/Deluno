using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;

namespace Deluno.Worker.Services;

/// <summary>
/// After Deluno refuses a release, it looks for another copy of that title
/// straight away (#604).
///
/// <para>A processor's refusal always did; a refusal at import - a sample, no
/// video, an unsupported file - blocklisted the release and then waited for the
/// title's turn in the scheduled search. On the soak rig Breaking Bad S03E03's
/// sample was refused at 16:32 and its real copy, on the other indexer since the
/// same minute, was still not fetched two hours later. One search, shared, so
/// the two refusals cannot drift apart.</para>
///
/// <para>A search aimed at one title: the library search reaches a film by its
/// id and an episode by its own id, and it still holds a title the processor
/// keeps refusing (#583).</para>
/// </summary>
public static class AnotherCopySearch
{
    public static Task<JobQueueItem> EnqueueAsync(
        IJobScheduler jobScheduler,
        DownloadDispatchItem dispatch,
        string libraryName,
        string triggeredBy,
        string key,
        CancellationToken cancellationToken)
        => jobScheduler.EnqueueAsync(
            new EnqueueJobRequest(
                LibrarySearchJobTypes.For(dispatch.MediaType),
                triggeredBy,
                JsonSerializer.Serialize(new
                {
                    libraryId = dispatch.LibraryId,
                    libraryName,
                    mediaType = dispatch.MediaType,
                    checkMissing = true,
                    checkUpgrades = true,
                    maxItems = 1,
                    retryDelayHours = 24,
                    triggeredBy,
                    targetEntityId = dispatch.EntityId
                }),
                "download_dispatch",
                dispatch.Id,
                IdempotencyKey: $"{triggeredBy}:{key}",
                DedupeKey: $"{triggeredBy}:{key}"),
            cancellationToken);
}
