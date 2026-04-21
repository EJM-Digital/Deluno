using Deluno.Platform.Contracts;

namespace Deluno.Platform.Data;

public interface IPlatformSettingsRepository
{
    Task<PlatformSettingsSnapshot> GetAsync(CancellationToken cancellationToken);

    Task<PlatformSettingsSnapshot> SaveAsync(
        UpdatePlatformSettingsRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<LibraryItem>> ListLibrariesAsync(CancellationToken cancellationToken);

    Task<LibraryItem> CreateLibraryAsync(
        CreateLibraryRequest request,
        CancellationToken cancellationToken);

    Task<LibraryItem?> UpdateLibraryAutomationAsync(
        string id,
        UpdateLibraryAutomationRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ConnectionItem>> ListConnectionsAsync(CancellationToken cancellationToken);

    Task<ConnectionItem> CreateConnectionAsync(
        CreateConnectionRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<IndexerItem>> ListIndexersAsync(CancellationToken cancellationToken);

    Task<IndexerItem> CreateIndexerAsync(
        CreateIndexerRequest request,
        CancellationToken cancellationToken);

    Task<bool> DeleteLibraryAsync(string id, CancellationToken cancellationToken);

    Task<bool> DeleteConnectionAsync(string id, CancellationToken cancellationToken);

    Task<bool> DeleteIndexerAsync(string id, CancellationToken cancellationToken);
}
