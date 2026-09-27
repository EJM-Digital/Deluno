namespace Deluno.Connections.Contracts;

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
    bool? IsEnabled,
    bool? UseSsl = null,
    string? UrlBase = null,
    bool? AddPaused = null,
    /// <summary>Comma-separated tag names. Null leaves the saved tags unchanged; an empty string clears them.</summary>
    string? Tags = null);
