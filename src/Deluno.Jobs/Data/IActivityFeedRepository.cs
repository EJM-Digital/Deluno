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

    /// <summary>
    /// The Activity history (#1100): newest first, paged, filtered by kind, title, file, source, outcome,
    /// date range and (for turned-down releases) the reason group. Every filter combines with the rest.
    /// </summary>
    // Default falls back to the entity filters so a test double need not implement it; SqliteJobStore does the real work.
    Task<Page<ActivityEventItem>> ListActivityPageAsync(
        PageRequest request,
        ActivityQuery query,
        CancellationToken cancellationToken)
        => ListActivityPageAsync(request, query.RelatedEntityType, query.RelatedEntityId, cancellationToken);

    Task<ActivityEventItem> RecordActivityAsync(
        string category,
        string message,
        string? detailsJson,
        string? relatedJobId,
        string? relatedEntityType,
        string? relatedEntityId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes a line about a condition that holds for as long as it holds (#1083):
    /// once when it starts, again only when what it says changes, never on every
    /// check cycle. A line is skipped when the newest line of the same category
    /// about the same item already says exactly this. Returns null when skipped.
    /// </summary>
    // Default records every time, so a test double need not implement the check; SqliteJobStore does.
    async Task<ActivityEventItem?> RecordActivityOnChangeAsync(
        string category,
        string message,
        string? detailsJson,
        string? relatedJobId,
        string? relatedEntityType,
        string? relatedEntityId,
        CancellationToken cancellationToken)
        => await RecordActivityAsync(category, message, detailsJson, relatedJobId, relatedEntityType, relatedEntityId, cancellationToken);

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

    /// <summary>
    /// Removes every stored Activity row (#871). This is the same table
    /// decision explanations (DecisionExplanationActivity) are recorded to,
    /// so clearing the timeline clears that automation-decision history too
    /// -- there is nothing else it lives in to spare.
    /// </summary>
    Task<int> ClearActivityAsync(CancellationToken cancellationToken);

    /// <summary>Aggregated counts of what happened since <paramref name="sinceUtc"/>, a window <paramref name="windowHours"/> long (#827).</summary>
    Task<ActivityStatsSnapshot> GetActivityStatsAsync(
        DateTimeOffset sinceUtc,
        int windowHours,
        CancellationToken cancellationToken);

    /// <summary>
    /// Counts of Activity rows since <paramref name="sinceUtc"/> by the hour of the day
    /// (shifted by <paramref name="offsetMinutes"/> from UTC) and category, for the
    /// Dashboard's "Today" bars (#1096). One indexed range read and a small GROUP BY.
    /// </summary>
    // Default empty so test doubles need not implement it; SqliteJobStore does the real work.
    Task<IReadOnlyList<ActivityHourCount>> GetActivityCountsByHourAsync(
        DateTimeOffset sinceUtc,
        int offsetMinutes,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ActivityHourCount>>([]);
}
