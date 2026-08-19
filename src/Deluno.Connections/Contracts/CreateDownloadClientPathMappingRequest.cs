namespace Deluno.Connections.Contracts;

public sealed record CreateDownloadClientPathMappingRequest(
    string? RemotePath,
    string? LocalPath,
    bool IsEnabled,
    int? Priority);
