namespace Deluno.Platform.Contracts;

public sealed record CreateDownloadClientRequest(
    string? Name,
    string? Protocol,
    string? EndpointUrl,
    string? CategoryTemplate,
    int? Priority,
    bool IsEnabled);
