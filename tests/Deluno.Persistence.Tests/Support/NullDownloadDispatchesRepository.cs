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

    public Task<IReadOnlyList<DownloadDispatchItem>> FindUnresolvedDispatchesAsync(
        int minAgeMinutes,
        string? clientId,
        int limit,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DownloadDispatchItem>>(new List<DownloadDispatchItem>());

    public Task<DownloadDispatchItem> RecordGrabAsync(
        string dispatchId,
        string grabStatus,
        int? grabResponseCode,
        string? grabMessage,
        string? grabFailureCode,
        string? grabResponseJson,
        CancellationToken cancellationToken) =>
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
            TorrentHashOrItemId: null,
            DownloadedBytes: null,
            ImportStatus: null,
            ImportDetectedUtc: null,
            ImportCompletedUtc: null,
            ImportedFilePath: null,
            ImportFailureCode: null,
            ImportFailureMessage: null,
            CircuitOpenUntilUtc: null));

    public Task<DownloadDispatchItem> RecordDetectionAsync(
        string dispatchId,
        string? torrentHashOrItemId,
        long? downloadedBytes,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException("Test only - should not be called");

    public Task<DownloadDispatchItem> RecordImportOutcomeAsync(
        string dispatchId,
        string importStatus,
        string? importedFilePath,
        string? importFailureCode,
        string? importFailureMessage,
        CancellationToken cancellationToken) =>
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

    public Task<DownloadDispatchItem?> FindDispatchByReleaseNameAsync(
        string clientId,
        string releaseName,
        CancellationToken cancellationToken) =>
        Task.FromResult<DownloadDispatchItem?>(null);

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

    public Task<DownloadDispatchItem> UpdateFailureRetryWindowAsync(
        string dispatchId,
        DateTimeOffset nextRetryEligibleUtc,
        int retryCount,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException("Test only - should not be called");

    public Task<IReadOnlyList<DownloadDispatchItem>> FindDispatchesEligibleForRetryAsync(
        int limit,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DownloadDispatchItem>>(new List<DownloadDispatchItem>());
}
