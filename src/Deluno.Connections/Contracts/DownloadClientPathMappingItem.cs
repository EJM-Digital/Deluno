namespace Deluno.Connections.Contracts;

/// <summary>
/// Translates the completed-download path reported by one download client into
/// the path visible to the Deluno host. This is needed when containers, NAS
/// shares, or separate machines mount the same files at different locations.
///
/// <paramref name="Host"/> is the host of the client this mapping was created
/// under (#809), lowercased and trimmed. A mapping applies to every client at
/// that host, not only <see cref="DownloadClientId"/> — the id is kept as the
/// mapping's original owner (and the fallback match for a client with no host
/// recorded), never as the only client it can apply to.
/// </summary>
public sealed record DownloadClientPathMappingItem(
    string Id,
    string DownloadClientId,
    string RemotePath,
    string LocalPath,
    bool IsEnabled,
    int Priority,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    string? Host = null);
