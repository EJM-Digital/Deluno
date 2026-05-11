using Deluno.Jobs.Data;

namespace Deluno.Persistence.Tests.Support;

public sealed class NullImportResolutionsRepository : IImportResolutionsRepository
{
    public Task<ImportResolution> RecordSuccessAsync(
        string dispatchId,
        string mediaType,
        string catalogId,
        string catalogItemType,
        CancellationToken cancellationToken) =>
        Task.FromResult(new ImportResolution
        {
            Id = $"res-{Guid.NewGuid():N}".Substring(0, 20),
            DispatchId = dispatchId,
            MediaType = mediaType,
            CatalogId = catalogId,
            CatalogItemType = catalogItemType,
            ImportAttemptUtc = DateTimeOffset.UtcNow,
            ImportSuccessUtc = DateTimeOffset.UtcNow,
            CreatedUtc = DateTimeOffset.UtcNow
        });

    public Task<ImportResolution> RecordFailureAsync(
        string dispatchId,
        string mediaType,
        string catalogId,
        string catalogItemType,
        string? failureCode,
        string? failureMessage,
        CancellationToken cancellationToken) =>
        Task.FromResult(new ImportResolution
        {
            Id = $"res-{Guid.NewGuid():N}".Substring(0, 20),
            DispatchId = dispatchId,
            MediaType = mediaType,
            CatalogId = catalogId,
            CatalogItemType = catalogItemType,
            ImportAttemptUtc = DateTimeOffset.UtcNow,
            ImportFailureUtc = DateTimeOffset.UtcNow,
            FailureCode = failureCode,
            FailureMessage = failureMessage,
            CreatedUtc = DateTimeOffset.UtcNow
        });

    public Task<IReadOnlyList<ImportResolution>> GetDispatchResolutionsAsync(
        string dispatchId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ImportResolution>>(new List<ImportResolution>());

    public Task<IReadOnlyList<ImportResolution>> GetCatalogItemResolutionsAsync(
        string mediaType,
        string catalogId,
        string catalogItemType,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ImportResolution>>(new List<ImportResolution>());

    public Task<IReadOnlyList<ImportResolution>> FindSuccessfulResolutionsSinceAsync(
        DateTimeOffset since,
        int limit,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ImportResolution>>(new List<ImportResolution>());

    public Task<IReadOnlyList<ImportResolution>> FindFailedResolutionsSinceAsync(
        DateTimeOffset since,
        int limit,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ImportResolution>>(new List<ImportResolution>());
}
