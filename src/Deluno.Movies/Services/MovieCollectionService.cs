using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.Metadata;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Quality;
using Deluno.Quality.Data;

namespace Deluno.Movies.Services;

public sealed class MovieCollectionService(
    IMovieCollectionsRepository collectionsRepository,
    IMetadataProvider metadataProvider,
    IMovieCatalogRepository movieCatalogRepository,
    ILibrariesRepository librariesRepository,
    IQualityRepository qualityRepository,
    IMediaDecisionService mediaDecisionService,
    IJobQueueRepository jobQueueRepository,
    TimeProvider timeProvider) : IMovieCollectionService
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromHours(6);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<MovieCollectionItem?> CreateOrUpdateAsync(
        CreateMovieCollectionRequest request,
        CancellationToken cancellationToken)
    {
        var providerId = request.ProviderId?.Trim();
        if (string.IsNullOrWhiteSpace(providerId))
        {
            throw new ArgumentException("A TMDb collection id is required.", nameof(request));
        }

        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        var library = libraries.FirstOrDefault(item =>
            string.Equals(item.Id, request.LibraryId?.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.MediaType, "movies", StringComparison.OrdinalIgnoreCase));
        if (library is null)
        {
            throw new ArgumentException("Choose a movie library for the collection.", nameof(request));
        }

        var metadata = await metadataProvider.GetMovieCollectionAsync(providerId, cancellationToken);
        if (metadata is null)
        {
            return null;
        }

        var collection = await collectionsRepository.UpsertAsync(
            library.Id,
            library.Name,
            library.RootPath,
            request.QualityProfileId ?? library.QualityProfileId,
            library.QualityProfileName,
            request,
            metadata,
            cancellationToken);

        // Populate the page immediately. Monitoring controls whether the
        // existing automation cycle adds missing members; it should not hide
        // the provider's full membership until that cycle happens to run.
        await collectionsRepository.SaveSnapshotAsync(
            collection.Id,
            metadata,
            timeProvider.GetUtcNow(),
            cancellationToken);
        return await collectionsRepository.GetAsync(collection.Id, cancellationToken) ?? collection;
    }

    public async Task<MovieCollectionSyncResult> SyncAsync(
        string collectionId,
        CancellationToken cancellationToken)
    {
        var collection = await collectionsRepository.GetAsync(collectionId, cancellationToken)
            ?? throw new InvalidOperationException("Movie collection not found.");
        var now = timeProvider.GetUtcNow();

        try
        {
            var metadata = await metadataProvider.GetMovieCollectionAsync(collection.ProviderId, cancellationToken);
            if (metadata is null)
            {
                await collectionsRepository.RecordSyncResultAsync(
                    collection.Id,
                    now.Add(RetryInterval),
                    null,
                    "The metadata provider did not return this collection.",
                    cancellationToken);
                return new MovieCollectionSyncResult(
                    collection.Id,
                    collection.MemberCount,
                    0,
                    collection.HeldCount,
                    0,
                    false,
                    "The metadata provider did not return this collection; Deluno will try again later.");
            }

            await collectionsRepository.SaveSnapshotAsync(collection.Id, metadata, now, cancellationToken);
            var members = await collectionsRepository.ListMembersAsync(collection.Id, cancellationToken);
            var library = (await librariesRepository.ListLibrariesAsync(cancellationToken))
                .FirstOrDefault(item => string.Equals(item.Id, collection.LibraryId, StringComparison.OrdinalIgnoreCase));
            if (library is null)
            {
                throw new InvalidOperationException("The movie library assigned to this collection no longer exists.");
            }

            var quality = await ResolveQualityAsync(collection, library, cancellationToken);

            var added = 0;
            var linked = 0;
            var excluded = 0;
            foreach (var member in members)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (member.IsExcluded)
                {
                    excluded++;
                    continue;
                }

                var movieId = member.LocalMovieId;
                MovieListItem? localMovie = null;
                if (string.IsNullOrWhiteSpace(movieId))
                {
                    movieId = await movieCatalogRepository.FindExistingIdAsync(
                        member.Title,
                        member.ReleaseYear,
                        member.ImdbId,
                        collection.Provider,
                        member.ProviderId,
                        cancellationToken);
                }

                if (string.IsNullOrWhiteSpace(movieId) && collection.Monitored && collection.MonitorMovies)
                {
                    localMovie = await movieCatalogRepository.AddAsync(
                        new CreateMovieRequest(
                            Title: member.Title,
                            ReleaseYear: member.ReleaseYear,
                            ImdbId: member.ImdbId,
                            Monitored: true,
                            MetadataProvider: collection.Provider,
                            MetadataProviderId: member.ProviderId,
                            Overview: member.Overview,
                            PosterUrl: member.PosterUrl,
                            BackdropUrl: member.BackdropUrl,
                            ExternalUrl: member.ExternalUrl,
                            MetadataJson: JsonSerializer.Serialize(new
                            {
                                provider = collection.Provider,
                                providerId = member.ProviderId,
                                collectionId = collection.ProviderId,
                                collectionName = collection.Name
                            }, JsonOptions)),
                        cancellationToken);
                    movieId = localMovie.Id;
                    added++;

                    if (!string.Equals(collection.MinimumAvailability, MovieAvailability.Released, StringComparison.OrdinalIgnoreCase))
                    {
                        await movieCatalogRepository.UpdateMinimumAvailabilityAsync(
                            localMovie.Id,
                            collection.MinimumAvailability,
                            cancellationToken);
                    }
                }

                if (string.IsNullOrWhiteSpace(movieId))
                {
                    continue;
                }

                if (await collectionsRepository.LinkMovieAsync(collection.Id, member.ProviderId, movieId, cancellationToken))
                {
                    linked++;
                }

                // Re-evaluate from the actual catalogue state. A collection
                // refresh must not turn an already-held movie back into
                // Missing simply because the provider membership is being
                // reconciled. This also lets the normal quality policy move a
                // held, below-cutoff title to Upgrade as it would elsewhere.
                localMovie ??= await movieCatalogRepository.GetByIdAsync(movieId, cancellationToken);
                var decision = mediaDecisionService.DecideWantedState(new MediaWantedDecisionInput(
                    MediaType: "movies",
                    HasFile: localMovie?.HasFile ?? false,
                    CurrentQuality: localMovie?.CurrentQuality,
                    CutoffQuality: quality.CutoffQuality,
                    UpgradeUntilCutoff: quality.UpgradeUntilCutoff,
                    UpgradeUnknownItems: quality.UpgradeUnknownItems,
                    IsReleased: localMovie?.IsAvailable ?? true));

                await movieCatalogRepository.EnsureWantedStateAsync(
                    movieId,
                    library.Id,
                    decision.WantedStatus,
                    decision.WantedReason,
                    false,
                    decision.CurrentQuality,
                    decision.TargetQuality,
                    decision.QualityCutoffMet,
                    cancellationToken);

                if (!string.IsNullOrWhiteSpace(collection.QualityProfileId))
                {
                    await movieCatalogRepository.UpdateQualityProfileAsync(
                        movieId,
                        collection.QualityProfileId,
                        cancellationToken);
                }
            }

            var searchRequested = false;
            if (added > 0 && collection.SearchOnAdd)
            {
                searchRequested = await jobQueueRepository.RequestLibrarySearchAsync(
                    ToAutomationPlan(library),
                    cancellationToken);
            }

            await collectionsRepository.RecordSyncResultAsync(
                collection.Id,
                now.Add(SyncInterval),
                now,
                null,
                cancellationToken);

            return new MovieCollectionSyncResult(
                collection.Id,
                members.Count,
                added,
                linked,
                excluded,
                searchRequested,
                added > 0
                    ? $"Collection refreshed and added {added} new movie{(added == 1 ? "" : "s")}."
                    : "Collection refreshed; all eligible members are already in the library.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await collectionsRepository.RecordSyncResultAsync(
                collection.Id,
                now.Add(RetryInterval),
                null,
                ex.Message,
                CancellationToken.None);
            return new MovieCollectionSyncResult(
                collection.Id,
                collection.MemberCount,
                0,
                0,
                0,
                false,
                $"Collection refresh failed and will retry later: {ex.Message}");
        }
    }

    private async Task<CollectionQuality> ResolveQualityAsync(
        MovieCollectionItem collection,
        Deluno.Libraries.Contracts.LibraryItem library,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(collection.QualityProfileId))
        {
            return new CollectionQuality(
                library.CutoffQuality,
                library.UpgradeUntilCutoff,
                library.UpgradeUnknownItems);
        }

        var profile = (await qualityRepository.ListQualityProfilesAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Id, collection.QualityProfileId, StringComparison.OrdinalIgnoreCase));
        return profile is null
            ? new CollectionQuality(library.CutoffQuality, library.UpgradeUntilCutoff, library.UpgradeUnknownItems)
            : new CollectionQuality(profile.CutoffQuality, profile.UpgradeUntilCutoff, profile.UpgradeUnknownItems);
    }

    private static LibraryAutomationPlanItem ToAutomationPlan(Deluno.Libraries.Contracts.LibraryItem library)
        => new(
            LibraryId: library.Id,
            LibraryName: library.Name,
            MediaType: library.MediaType,
            AutoSearchEnabled: library.AutoSearchEnabled,
            MissingSearchEnabled: library.MissingSearchEnabled,
            UpgradeSearchEnabled: library.UpgradeSearchEnabled,
            SearchIntervalHours: library.SearchIntervalHours,
            RetryDelayHours: library.RetryDelayHours,
            MaxItemsPerRun: library.MaxItemsPerRun,
            SearchWindowStartHour: library.SearchWindowStartHour,
            SearchWindowEndHour: library.SearchWindowEndHour);

    private sealed record CollectionQuality(
        string? CutoffQuality,
        bool UpgradeUntilCutoff,
        bool UpgradeUnknownItems);
}
