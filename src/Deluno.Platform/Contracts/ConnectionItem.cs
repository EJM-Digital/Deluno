namespace Deluno.Platform.Contracts;

public sealed record ConnectionItem(
    string Id,
    string Name,
    string ConnectionKind,
    string Role,
    string? EndpointUrl,
    bool IsEnabled,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);
