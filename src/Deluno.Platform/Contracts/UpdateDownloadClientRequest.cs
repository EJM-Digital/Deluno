namespace Deluno.Platform.Contracts;

/// <summary>
/// Patch-style request — null values mean "leave unchanged".
/// Sent by PUT /api/download-clients/{id}
/// </summary>
public sealed record UpdateDownloadClientRequest(
    string? Name,
    string? Protocol,
    string? Host,
    int? Port,
    string? Username,
    string? Password,
    string? EndpointUrl,
    string? MoviesCategory,
    string? TvCategory,
    string? CategoryTemplate,
    int? Priority,
    bool? IsEnabled);
