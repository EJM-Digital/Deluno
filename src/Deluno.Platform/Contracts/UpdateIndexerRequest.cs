namespace Deluno.Platform.Contracts;

/// <summary>
/// Patch-style request — null values mean "leave unchanged".
/// Sent by PUT /api/indexers/{id}
/// </summary>
public sealed record UpdateIndexerRequest(
    string? Name,
    string? Protocol,
    string? Privacy,
    string? BaseUrl,
    string? ApiKey,
    int? Priority,
    string? Categories,
    string? Tags,
    string? MediaScope,
    bool? IsEnabled);
