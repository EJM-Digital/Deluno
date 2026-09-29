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
    TimeProvider timeProvider,
    ITitleTagService? titleTagService = null,
    IActivityFeedRepository? activityFeedRepository = null,
    IAutoTaggingService? autoTagging = null) : IMovieCollectionService
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
            var taggedCount = 0;
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
                var addedNow = false;
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
                    addedNow = true;

                    // #1046: the collection's tags go on the movie the moment it exists,
                    // before its wanted state, its search or anything that reads its tags.
                    if (titleTagService is not null && TagNameList.Split(collection.Tags).Count > 0)
                    {
                        await titleTagService.GiveAsync(MediaKind.Movie, movieId, TagNameList.Split(collection.Tags), cancellationToken);
                        taggedCount++;
                    }

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

                // #1024: auto-tagging rules for a film this collection just added, after its
                // own tags and library are settled and before anything searches for it.
                if (addedNow && autoTagging is not null)
                {
                    await autoTagging.ApplyAsync(
                        MediaKind.Movie,
                        movieId,
                        AutoTagTrigger.Added,
                        new AutoTagAddFacts(false, false, collection.Name),
                        [library.Id],
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

            if (taggedCount > 0 && activityFeedRepository is not null)
            {
                await activityFeedRepository.RecordActivityAsync(
                    "collection.tagged",
                    $"{collection.Name} added {added} movie{(added == 1 ? "" : "s")} and gave {(added == 1 ? "it" : "them")} the tag{(TagNameList.Split(collection.Tags).Count == 1 ? "" : "s")} {string.Join(", ", TagNameList.Split(collection.Tags))}.",
                    null,
                    null,
                    "movie-collection",
                    collection.Id,
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

    public async Task<IReadOnlyList<MovieCollectionItem>> UpdateManyAsync(
        IReadOnlyList<string> collectionIds,
        UpdateMovieCollectionRequest changes,
        CancellationToken cancellationToken)
    {
        if (collectionIds.Count == 0)
        {
            throw new ArgumentException("Choose at least one collection.");
        }

        if (!string.IsNullOrWhiteSpace(changes.MinimumAvailability)
            && !MovieAvailability.All.Contains(changes.MinimumAvailability.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Choose one of: {string.Join(", ", MovieAvailability.All)}.");
        }

        string? profileName = null;
        if (!string.IsNullOrWhiteSpace(changes.QualityProfileId))
        {
            var profile = (await qualityRepository.ListQualityProfilesAsync(cancellationToken))
                .FirstOrDefault(item => string.Equals(item.Id, changes.QualityProfileId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (profile is null)
            {
                throw new ArgumentException("That quality profile no longer exists.");
            }

            profileName = profile.Name;
        }

        Deluno.Libraries.Contracts.LibraryItem? library = null;
        if (!string.IsNullOrWhiteSpace(changes.LibraryId))
        {
            library = (await librariesRepository.ListLibrariesAsync(cancellationToken)).FirstOrDefault(item =>
                string.Equals(item.Id, changes.LibraryId.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.MediaType, "movies", StringComparison.OrdinalIgnoreCase));
            if (library is null)
            {
                throw new ArgumentException("Choose a movie library for the collections.");
            }
        }

        var resolved = new MovieCollectionResolvedTargets(profileName, library?.Name, library?.RootPath);
        var normalized = changes with { LibraryId = library?.Id, QualityProfileId = changes.QualityProfileId?.Trim() };
        var updated = new List<MovieCollectionItem>();
        foreach (var id in collectionIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            MovieCollectionItem? item;
            try
            {
                item = await collectionsRepository.UpdateAsync(id, normalized, cancellationToken, resolved);
            }
            catch (Microsoft.Data.Sqlite.SqliteException) when (library is not null)
            {
                throw new ArgumentException($"A collection you picked is already followed in {library.Name}.");
            }

            if (item is not null)
            {
                updated.Add(item);
            }
        }

        if (updated.Count > 0 && activityFeedRepository is not null)
        {
            var parts = new List<string>();
            if (changes.Monitored is { } m) parts.Add(m ? "monitoring on" : "monitoring off");
            if (changes.MonitorMovies is { } mm) parts.Add(mm ? "adding missing movies on" : "adding missing movies off");
            if (profileName is not null) parts.Add($"quality profile {profileName}");
            if (!string.IsNullOrWhiteSpace(changes.MinimumAvailability)) parts.Add($"minimum availability {MovieAvailability.Normalize(changes.MinimumAvailability)}");
            if (library is not null) parts.Add($"folder {library.RootPath}");
            if (changes.SearchOnAdd is { } s) parts.Add(s ? "search on add on" : "search on add off");
            if (changes.Tags is not null) parts.Add("tags changed");
            if (parts.Count > 0)
            {
                await activityFeedRepository.RecordActivityAsync(
                    "collection.edited",
                    $"You changed {updated.Count} movie collection{(updated.Count == 1 ? "" : "s")}: {string.Join(", ", parts)}.",
                    null,
                    null,
                    "movie-collection",
                    updated.Count == 1 ? updated[0].Id : null,
                    cancellationToken);
            }
        }

        return updated;
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
            SearchIntervalMinutes: library.SearchIntervalMinutes,
            RetryDelayHours: library.RetryDelayHours,
            MaxItemsPerRun: library.MaxItemsPerRun,
            SearchWindowStartHour: library.SearchWindowStartHour,
            SearchWindowEndHour: library.SearchWindowEndHour);

    private sealed record CollectionQuality(
        string? CutoffQuality,
        bool UpgradeUntilCutoff,
        bool UpgradeUnknownItems);
}
