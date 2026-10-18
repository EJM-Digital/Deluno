using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;

namespace Deluno.Persistence.Tests.Support;

public sealed class NullDownloadDispatchesRepository : IDownloadDispatchesRepository
{
    public Task<DownloadDispatchItem?> GetDispatchAsync(string dispatchId, CancellationToken cancellationToken) =>
        Task.FromResult<DownloadDispatchItem?>(null);

    public Task<(IReadOnlyList<DownloadDispatchItem> Items, string? NextPageToken)> QueryDispatchesAsync(
        DispatchQueryFilter filter,
        DispatchPaginationOptions pagination,
        CancellationToken cancellationToken) =>
        Task.FromResult<(IReadOnlyList<DownloadDispatchItem>, string?)>((new List<DownloadDispatchItem>(), null));

    public Task<DownloadDispatchItem?> GetDispatchIncludingArchivedAsync(string dispatchId, CancellationToken cancellationToken) =>
        Task.FromResult<DownloadDispatchItem?>(null);

    public Task<IReadOnlyList<DownloadInFlight>> ListDownloadsInFlightAsync(
        int limit,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DownloadInFlight>>([]);

    public Task<DownloadDispatchItem> RecordGrabAsync(
        string dispatchId,
        string grabStatus,
        int? grabResponseCode,
        string? grabMessage,
        string? grabFailureCode,
        string? grabResponseJson,
        CancellationToken cancellationToken,
        IntegrationFailure? failure = null,
        string? externalId = null) =>
        Task.FromResult(new DownloadDispatchItem(
            Id: dispatchId,
            LibraryId: null!,
            MediaType: null!,
            EntityType: null!,
            EntityId: null!,
            ReleaseName: null!,
            IndexerName: null!,
            DownloadClientId: null!,
            DownloadClientName: null!,
            Status: "grabbed",
            NotesJson: grabResponseJson,
            CreatedUtc: DateTimeOffset.UtcNow,
            GrabStatus: grabStatus,
            GrabAttemptedUtc: DateTimeOffset.UtcNow,
            GrabResponseCode: grabResponseCode,
            GrabMessage: grabMessage,
            GrabFailureCode: grabFailureCode,
            GrabResponseJson: grabResponseJson,
            DetectedUtc: null,
            TorrentHashOrItemId: externalId,
            DownloadedBytes: null,
            ImportStatus: null,
            ImportDetectedUtc: null,
            ImportCompletedUtc: null,
            ImportedFilePath: null,
            ImportFailureCode: null,
            ImportFailureMessage: null,
            CircuitOpenUntilUtc: null,
            Failure: failure));

    public Task<DownloadDispatchItem> RecordDetectionAsync(
        string dispatchId,
        string? torrentHashOrItemId,
        long? downloadedBytes,
        bool completed,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException("Test only - should not be called");

    public Task<DownloadDispatchItem> RecordImportOutcomeAsync(
        string dispatchId,
        string importStatus,
        string? importedFilePath,
        string? importFailureCode,
        string? importFailureMessage,
        CancellationToken cancellationToken,
        IntegrationFailure? failure = null,
        long? importedFileBytes = null) =>
        throw new NotImplementedException("Test only - should not be called");

    public Task<IReadOnlyList<DispatchTimelineEvent>> GetDispatchTimelineAsync(
        string dispatchId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DispatchTimelineEvent>>(new List<DispatchTimelineEvent>());

    public Task<DispatchTimelineEvent> RecordTimelineEventAsync(
        string dispatchId,
        string eventType,
        string? detailsJson,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException("Test only - should not be called");

    public Task<DownloadDispatchItem> SetCircuitBreakerAsync(
        string dispatchId,
        DateTimeOffset? openUntilUtc,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException("Test only - should not be called");

    public Task<bool> MarkNeverDetectedAsync(string dispatchId, string message, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task ArchiveDispatchAsync(
        string dispatchId,
        string reason,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<DownloadDispatchItem?> FindDispatchByHashAsync(
        string clientId,
        string hash,
        CancellationToken cancellationToken) =>
        Task.FromResult<DownloadDispatchItem?>(null);

    public Task<DownloadDispatchItem?> FindAnyDispatchByHashAsync(
        string clientId,
        string hash,
        CancellationToken cancellationToken) =>
        Task.FromResult<DownloadDispatchItem?>(null);

    public Task ReleaseDispatchesByHashAsync(string clientId, string hash, string message, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<string>> FindGrabbedReleaseNamesAsync(
        IReadOnlyCollection<string> candidateNames,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<string> ReserveGrabAsync(string clientId, string clientItemId, string releaseName, CancellationToken cancellationToken) =>
        Task.FromResult(Guid.NewGuid().ToString("N"));

    public Task ReleaseGrabReservationAsync(string reservationId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<bool> HasOtherGrabReservationAsync(string clientId, string clientItemId, string? exceptReservationId, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task<bool> ReplaceClientItemIdAsync(
        string dispatchId,
        string oldId,
        string newId,
        CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task<IReadOnlyList<DownloadDispatchItem>> FindDispatchesByClientItemIdsAsync(
        string clientId,
        IReadOnlyCollection<string> clientItemIds,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DownloadDispatchItem>>([]);

    public Task<IReadOnlyList<DownloadDispatchItem>> FindStaleFailedDispatchesAsync(
        TimeSpan minAge,
        int limit,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DownloadDispatchItem>>(new List<DownloadDispatchItem>());

    public Task<IReadOnlyList<DownloadDispatchItem>> FindOldUnresolvedDispatchesAsync(
        TimeSpan minAge,
        int limit,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DownloadDispatchItem>>(new List<DownloadDispatchItem>());
}
