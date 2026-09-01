using Deluno.Integrations.Metadata;
using Deluno.Movies.Contracts;

namespace Deluno.Movies.Data;

public interface IMovieCollectionsRepository
{
    Task<IReadOnlyList<MovieCollectionItem>> ListAsync(CancellationToken cancellationToken);

    Task<MovieCollectionItem?> GetAsync(string id, CancellationToken cancellationToken);

    Task<MovieCollectionItem> UpsertAsync(
        string libraryId,
        string libraryName,
        string rootPath,
        string? qualityProfileId,
        string? qualityProfileName,
        CreateMovieCollectionRequest request,
        MetadataCollection metadata,
        CancellationToken cancellationToken);

    /// <summary>
    /// Claims due collections by moving their next due time forward in the
    /// same transaction as the read. This makes the existing automation tick
    /// safe across restarts and multiple worker instances.
    /// </summary>
    Task<IReadOnlyList<MovieCollectionItem>> ClaimDueAsync(
        DateTimeOffset now,
        TimeSpan interval,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MovieCollectionMemberItem>> ListMembersAsync(
        string collectionId,
        CancellationToken cancellationToken);

    Task SaveSnapshotAsync(
        string collectionId,
        MetadataCollection metadata,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<MovieCollectionItem?> UpdateAsync(
        string id,
        UpdateMovieCollectionRequest request,
        CancellationToken cancellationToken);

    Task<bool> LinkMovieAsync(
        string collectionId,
        string providerId,
        string movieId,
        CancellationToken cancellationToken);

    Task<bool> SetMemberExcludedAsync(
        string collectionId,
        string providerId,
        bool excluded,
        CancellationToken cancellationToken);

    Task RecordSyncResultAsync(
        string collectionId,
        DateTimeOffset nextSyncUtc,
        DateTimeOffset? lastSyncedUtc,
        string? error,
        CancellationToken cancellationToken);
}
