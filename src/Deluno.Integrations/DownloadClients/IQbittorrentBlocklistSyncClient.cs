using Deluno.Connections.Contracts;

namespace Deluno.Integrations.DownloadClients;

/// <summary>
/// #822: pushing the content blocklist into qBittorrent's own "Excluded file
/// names" preference is qBittorrent-specific - no other client has an
/// equivalent setting - so it lives on its own small interface rather than on
/// <see cref="IDownloadClient"/>, and only <c>QbittorrentDownloadClient</c>
/// implements it.
/// </summary>
public interface IQbittorrentBlocklistSyncClient
{
    /// <summary>
    /// Sets qBittorrent's <c>excluded_file_names</c> preference to exactly
    /// these patterns (one per line, its own format), without touching
    /// whether that preference is itself turned on. Never throws for an
    /// ordinary failure to reach or authenticate with the client.
    /// </summary>
    Task<(bool Succeeded, string? Message)> PushExcludedFileNamesAsync(
        DownloadClientItem client,
        IReadOnlyList<string> patterns,
        CancellationToken cancellationToken);
}
