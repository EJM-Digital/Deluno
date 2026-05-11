namespace Deluno.Jobs.Data;

public interface IImportResolutionsRepository
{
    /// <summary>
    /// Record an import resolution (successful mapping of dispatch to catalog item).
    /// </summary>
    Task<ImportResolution> RecordSuccessAsync(
        string dispatchId,
        string mediaType,
        string catalogId,
        string catalogItemType,
        CancellationToken cancellationToken);

    /// <summary>
    /// Record an import failure resolution.
    /// </summary>
    Task<ImportResolution> RecordFailureAsync(
        string dispatchId,
        string mediaType,
        string catalogId,
        string catalogItemType,
        string? failureCode,
        string? failureMessage,
        CancellationToken cancellationToken);

    /// <summary>
    /// Get resolutions for a dispatch.
    /// </summary>
    Task<IReadOnlyList<ImportResolution>> GetDispatchResolutionsAsync(
        string dispatchId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Get resolutions for a catalog item.
    /// </summary>
    Task<IReadOnlyList<ImportResolution>> GetCatalogItemResolutionsAsync(
        string mediaType,
        string catalogId,
        string catalogItemType,
        CancellationToken cancellationToken);

    /// <summary>
    /// Find successful resolutions since a given time.
    /// </summary>
    Task<IReadOnlyList<ImportResolution>> FindSuccessfulResolutionsSinceAsync(
        DateTimeOffset since,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Find failed resolutions since a given time.
    /// </summary>
    Task<IReadOnlyList<ImportResolution>> FindFailedResolutionsSinceAsync(
        DateTimeOffset since,
        int limit,
        CancellationToken cancellationToken);
}

public class ImportResolution
{
    public required string Id { get; set; }
    public required string DispatchId { get; set; }
    public required string MediaType { get; set; }
    public required string CatalogId { get; set; }
    public required string CatalogItemType { get; set; }
    public required DateTimeOffset ImportAttemptUtc { get; set; }
    public DateTimeOffset? ImportSuccessUtc { get; set; }
    public DateTimeOffset? ImportFailureUtc { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }
    public required DateTimeOffset CreatedUtc { get; set; }

    public bool IsSuccessful => ImportSuccessUtc.HasValue;
}
