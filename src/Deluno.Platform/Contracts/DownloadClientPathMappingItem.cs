namespace Deluno.Platform.Contracts;

/// <summary>
/// Translates the completed-download path reported by one download client into
/// the path visible to the Deluno host. This is needed when containers, NAS
/// shares, or separate machines mount the same files at different locations.
/// </summary>
public sealed record DownloadClientPathMappingItem(
    string Id,
    string DownloadClientId,
    string RemotePath,
    string LocalPath,
    bool IsEnabled,
    int Priority,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);
