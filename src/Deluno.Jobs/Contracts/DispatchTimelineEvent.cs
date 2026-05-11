namespace Deluno.Jobs.Contracts;

public sealed record DispatchTimelineEvent(
    string Id,
    string DispatchId,
    string EventType,
    DateTimeOffset Timestamp,
    string? DetailsJson,
    DateTimeOffset CreatedUtc);
