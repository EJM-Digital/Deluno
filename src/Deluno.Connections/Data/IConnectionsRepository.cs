using Deluno.Connections.Contracts;

namespace Deluno.Connections.Data;

public interface IConnectionsRepository
{
    Task<IReadOnlyList<ConnectionItem>> ListConnectionsAsync(CancellationToken cancellationToken);

    Task<ConnectionItem> CreateConnectionAsync(
        CreateConnectionRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<IndexerItem>> ListIndexersAsync(CancellationToken cancellationToken);

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
        CancellationToken cancellationToken);

    Task<IndexerItem?> ResetIndexerCircuitAsync(string id, CancellationToken cancellationToken);

    Task<IndexerTestResult?> UpdateDownloadClientHealthAsync(
        string id,
        string healthStatus,
        string message,
        string? failureCategory,
        int? latencyMs,
        CancellationToken cancellationToken);

    Task<bool> DeleteConnectionAsync(string id, CancellationToken cancellationToken);
    Task<bool> DeleteIndexerAsync(string id, CancellationToken cancellationToken);
    Task<bool> DeleteDownloadClientAsync(string id, CancellationToken cancellationToken);
}
