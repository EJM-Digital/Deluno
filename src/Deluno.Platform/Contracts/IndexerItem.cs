namespace Deluno.Platform.Contracts;

public sealed record IndexerItem(
    string Id,
    string Name,
    string Protocol,
    string Privacy,
    string BaseUrl,
    int Priority,
    string Categories,
    string Tags,
    bool IsEnabled,
    string HealthStatus,
    string? LastHealthMessage,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);
