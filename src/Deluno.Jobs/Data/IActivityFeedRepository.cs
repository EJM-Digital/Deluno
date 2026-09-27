using Deluno.Contracts;
using Deluno.Jobs.Contracts;

namespace Deluno.Jobs.Data;

public interface IActivityFeedRepository
{
    Task<IReadOnlyList<ActivityEventItem>> ListActivityAsync(
        int take,
        string? relatedEntityType,
        string? relatedEntityId,
        CancellationToken cancellationToken);

    Task<Page<ActivityEventItem>> ListActivityPageAsync(
        PageRequest request,
        string? relatedEntityType,
        string? relatedEntityId,
        CancellationToken cancellationToken);

    Task<ActivityEventItem> RecordActivityAsync(
        string category,
        string message,
        string? detailsJson,
        string? relatedJobId,
        string? relatedEntityType,
        string? relatedEntityId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ActivityEventItem>> RecordActivityBatchAsync(
        IReadOnlyList<ActivityEventItem> activities,
        CancellationToken cancellationToken);

    /// <summary>
    /// #828: deletes every Activity row older than <paramref name="olderThan"/>.
    /// A different concept from a title's own history, which RetentionService
    /// governs; this never touches a title, a file, or anything but Deluno's
    /// own record of what it did and when.
    /// </summary>
    // Default no-op so test doubles need not implement pruning; SqliteJobStore does the real work.
    Task<int> PruneActivityAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
        => Task.FromResult(0);
}
