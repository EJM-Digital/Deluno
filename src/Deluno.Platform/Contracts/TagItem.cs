namespace Deluno.Platform.Contracts;

public sealed record TagItem(
    string Id,
    string Name,
    string Color,
    string Description,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);
