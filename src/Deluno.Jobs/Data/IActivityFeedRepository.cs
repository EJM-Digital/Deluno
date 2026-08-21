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
}
