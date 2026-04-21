using Deluno.Series.Contracts;

namespace Deluno.Series.Data;

public interface ISeriesCatalogRepository
{
    Task<SeriesListItem> AddAsync(CreateSeriesRequest request, CancellationToken cancellationToken);

    Task<SeriesListItem?> GetByIdAsync(string id, CancellationToken cancellationToken);

    Task<IReadOnlyList<SeriesListItem>> ListAsync(CancellationToken cancellationToken);

    Task<SeriesWantedSummary> GetWantedSummaryAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<SeriesWantedItem>> ListEligibleWantedAsync(
        string libraryId,
        int take,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task EnsureWantedStateAsync(
        string seriesId,
        string libraryId,
        string wantedStatus,
        string wantedReason,
        CancellationToken cancellationToken);

    Task<bool> ImportExistingAsync(
        string libraryId,
        string title,
        int? startYear,
        CancellationToken cancellationToken);

    Task RecordSearchAttemptAsync(
        string seriesId,
        string libraryId,
        string triggerKind,
        string outcome,
        DateTimeOffset now,
        DateTimeOffset? nextEligibleSearchUtc,
        string? lastSearchResult,
        CancellationToken cancellationToken);

    Task<SeriesImportRecoverySummary> GetImportRecoverySummaryAsync(CancellationToken cancellationToken);

    Task<SeriesImportRecoveryCase> AddImportRecoveryCaseAsync(
        CreateSeriesImportRecoveryCaseRequest request,
        CancellationToken cancellationToken);

    Task<bool> DeleteImportRecoveryCaseAsync(string id, CancellationToken cancellationToken);
}
