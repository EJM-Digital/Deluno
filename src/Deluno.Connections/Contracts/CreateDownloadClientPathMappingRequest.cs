namespace Deluno.Connections.Contracts;

/// <summary>
/// <paramref name="Host"/> is optional (#809): left blank, the mapping takes
/// the host of the client it is created under, which is what makes it
/// reusable for any other client at that same host. Set explicitly only when
/// a mapping needs to apply to a host other than the one the client it was
/// added under currently has.
/// </summary>
public sealed record CreateDownloadClientPathMappingRequest(
    string? RemotePath,
    string? LocalPath,
    bool IsEnabled,
    int? Priority,
    string? Host = null);
