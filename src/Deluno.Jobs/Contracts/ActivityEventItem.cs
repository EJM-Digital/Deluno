namespace Deluno.Jobs.Contracts;

public sealed record ActivityEventItem(
    string Id,
    string Category,
    string Message,
    string? DetailsJson,
    string? RelatedJobId,
    string? RelatedEntityType,
    string? RelatedEntityId,
    DateTimeOffset CreatedUtc);
