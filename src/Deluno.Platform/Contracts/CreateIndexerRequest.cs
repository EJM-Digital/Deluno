namespace Deluno.Platform.Contracts;

public sealed record CreateIndexerRequest(
    string? Name,
    string? Protocol,
    string? Privacy,
    string? BaseUrl,
    int? Priority,
    string? Categories,
    string? Tags,
    bool IsEnabled);
