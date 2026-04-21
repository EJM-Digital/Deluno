namespace Deluno.Platform.Contracts;

public sealed record DownloadClientItem(
    string Id,
    string Name,
    string Protocol,
    string? EndpointUrl,
    string? CategoryTemplate,
    int Priority,
    bool IsEnabled,
    string HealthStatus,
    string? LastHealthMessage,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);
