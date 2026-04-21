using Deluno.Movies.Contracts;

namespace Deluno.Movies.Data;

public interface IMovieCatalogRepository
{
    Task<MovieListItem> AddAsync(CreateMovieRequest request, CancellationToken cancellationToken);

    Task<MovieListItem?> GetByIdAsync(string id, CancellationToken cancellationToken);

    Task<IReadOnlyList<MovieListItem>> ListAsync(CancellationToken cancellationToken);

    Task<MovieWantedSummary> GetWantedSummaryAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<MovieWantedItem>> ListEligibleWantedAsync(
        string libraryId,
        int take,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task EnsureWantedStateAsync(
        string movieId,
        string libraryId,
        string wantedStatus,
        string wantedReason,
        bool hasFile,
        string? currentQuality,
        string? targetQuality,
        bool qualityCutoffMet,
        CancellationToken cancellationToken);

    Task<bool> ImportExistingAsync(
        string libraryId,
        string title,
        int? releaseYear,
        string wantedStatus,
        string wantedReason,
        string? currentQuality,
        string? targetQuality,
        bool qualityCutoffMet,
        CancellationToken cancellationToken);

    Task RecordSearchAttemptAsync(
        string movieId,
        string libraryId,
        string triggerKind,
        string outcome,
        DateTimeOffset now,
        DateTimeOffset? nextEligibleSearchUtc,
        string? lastSearchResult,
        CancellationToken cancellationToken);

    Task<int> ReevaluateLibraryWantedStateAsync(
        string libraryId,
        string? cutoffQuality,
        bool upgradeUntilCutoff,
        bool upgradeUnknownItems,
        CancellationToken cancellationToken);

    Task<MovieImportRecoverySummary> GetImportRecoverySummaryAsync(CancellationToken cancellationToken);

    Task<MovieImportRecoveryCase> AddImportRecoveryCaseAsync(
        CreateMovieImportRecoveryCaseRequest request,
        CancellationToken cancellationToken);

    Task<bool> DeleteImportRecoveryCaseAsync(string id, CancellationToken cancellationToken);
}
