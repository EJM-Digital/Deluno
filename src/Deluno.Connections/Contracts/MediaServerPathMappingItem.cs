namespace Deluno.Connections.Contracts;

/// <summary>
/// One folder Deluno and a media server disagree about the name of (#662) -
/// the same shape as a download-client path mapping
/// (<see cref="DownloadClientPathMappingItem"/>), just facing the other
/// direction: a Deluno path in, the path the server's own filesystem uses
/// out.
/// </summary>
public sealed record MediaServerPathMappingItem(
    string Id,
    string MediaServerId,
    string DelunoPath,
    string ServerPath,
    bool IsEnabled,
    int Priority,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

/// <param name="DelunoPath">The path prefix as Deluno's own filesystem sees it.</param>
/// <param name="ServerPath">What that same folder is called on the media server's filesystem.</param>
public sealed record CreateMediaServerPathMappingRequest(
    string? DelunoPath,
    string? ServerPath,
    bool IsEnabled,
    int? Priority);
