using Deluno.Connections.Contracts;
using Deluno.Contracts;

namespace Deluno.Connections.Data;

public interface IConnectionsRepository
{
    Task<IReadOnlyList<ConnectionItem>> ListConnectionsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<IndexerItem>> ListIndexersAsync(CancellationToken cancellationToken);

    /// <summary>
    /// #1076: gives every indexer the name saving it would give it today (a tracker's own name,
    /// its site, its tracker behind Jackett or Prowlarr). Safe to run every start.
    /// Returns how many were renamed.
    /// </summary>
    Task<int> ApplyAutomaticIndexerNamesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// #1076: keeps the name Prowlarr or Jackett gave an indexer (for the address it has now) and
    /// names the indexer after it. Returns the indexer, or null when there is none.
    /// </summary>
    Task<IndexerItem?> SetDiscoveredIndexerNameAsync(string id, string discoveredName, CancellationToken cancellationToken);

    Task<IndexerItem> CreateIndexerAsync(
        CreateIndexerRequest request,
        CancellationToken cancellationToken);

    Task<IndexerItem?> UpdateIndexerAsync(
        string id,
        UpdateIndexerRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DownloadClientItem>> ListDownloadClientsAsync(CancellationToken cancellationToken);

    Task<DownloadClientItem> CreateDownloadClientAsync(
        CreateDownloadClientRequest request,
        CancellationToken cancellationToken);

    Task<DownloadClientItem?> UpdateDownloadClientAsync(
        string id,
        UpdateDownloadClientRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DownloadClientPathMappingItem>> ListDownloadClientPathMappingsAsync(
        string? downloadClientId,
        CancellationToken cancellationToken);

    Task<DownloadClientPathMappingItem?> CreateDownloadClientPathMappingAsync(
        string downloadClientId,
        CreateDownloadClientPathMappingRequest request,
        CancellationToken cancellationToken);

    Task<bool> DeleteDownloadClientPathMappingAsync(
        string downloadClientId,
        string id,
        CancellationToken cancellationToken);

    Task<IndexerTestResult?> UpdateIndexerHealthAsync(
        string id,
        string healthStatus,
        string message,
        string? failureCategory,
        int? latencyMs,
        CancellationToken cancellationToken,
        IntegrationFailure? failure = null);

    Task<IndexerItem?> ResetIndexerCircuitAsync(string id, CancellationToken cancellationToken);

    Task<IndexerTestResult?> UpdateDownloadClientHealthAsync(
        string id,
        string healthStatus,
        string message,
        string? failureCategory,
        int? latencyMs,
        CancellationToken cancellationToken,
        IntegrationFailure? failure = null);

    Task<bool> DeleteIndexerAsync(string id, CancellationToken cancellationToken);
    Task<bool> DeleteDownloadClientAsync(string id, CancellationToken cancellationToken);

    /// <summary>Routes a download client that no library sends to yet to the libraries of the types it serves (a Usenet client waiting for its provider, golden path rc.30.9). Returns how many libraries now send to it.</summary>
    Task<int> RouteUnroutedDownloadClientAsync(string id, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProxyItem>> ListProxiesAsync(CancellationToken cancellationToken);

    Task<ProxyItem> CreateProxyAsync(CreateProxyRequest request, CancellationToken cancellationToken);

    Task<ProxyItem?> UpdateProxyAsync(string id, UpdateProxyRequest request, CancellationToken cancellationToken);

    /// <summary>Removes the proxy and unassigns it from any indexer that was using it.</summary>
    Task<bool> DeleteProxyAsync(string id, CancellationToken cancellationToken);
}
