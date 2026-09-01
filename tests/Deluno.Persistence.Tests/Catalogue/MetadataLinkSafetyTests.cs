using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.Metadata;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;
using MovieRepository = Deluno.Movies.Data.SqliteMovieCatalogRepository;
using SeriesRepository = Deluno.Series.Data.SqliteSeriesCatalogRepository;

namespace Deluno.Persistence.Tests.Catalogue;

public sealed class MetadataLinkSafetyTests
{
    [Fact]
    public async Task Movie_remap_conflicts_identify_provider_imdb_and_title_owners_without_matching_itself()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-01T05:00:00Z"));
        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new MovieRepository(storage.Factory, clock);
        var current = await repository.AddAsync(
            new CreateMovieRequest("Current Movie", 2020, "tt0000001", MetadataProvider: "tmdb", MetadataProviderId: "1"),
            CancellationToken.None);
        var held = await repository.AddAsync(
            new CreateMovieRequest("Held Movie", 2021, "tt0000002", MetadataProvider: "tmdb", MetadataProviderId: "2"),
            CancellationToken.None);

        var providerConflict = await repository.FindMetadataIdentityConflictAsync(
            current.Id, "Different", 2022, "tt9999999", "tmdb", "2", CancellationToken.None);
        var imdbConflict = await repository.FindMetadataIdentityConflictAsync(
            current.Id, "Different", 2022, held.ImdbId, "tmdb", "999", CancellationToken.None);
        var titleConflict = await repository.FindMetadataIdentityConflictAsync(
            current.Id, held.Title, held.ReleaseYear, "tt9999999", "tmdb", "999", CancellationToken.None);
        var self = await repository.FindMetadataIdentityConflictAsync(
            current.Id, current.Title, current.ReleaseYear, current.ImdbId, "tmdb", "1", CancellationToken.None);

        Assert.Equal((held.Id, "provider-id"), (providerConflict!.Id, providerConflict.Reason));
        Assert.Equal((held.Id, "imdb-id"), (imdbConflict!.Id, imdbConflict.Reason));
        Assert.Equal((held.Id, "title-year"), (titleConflict!.Id, titleConflict.Reason));
        Assert.Null(self);

        var proposed = Metadata("99", "One Movie Identity", "movies", 2025, "tt0000099");
        Assert.NotNull(await repository.UpdateMetadataAsync(current.Id, proposed, CancellationToken.None));
        await Assert.ThrowsAsync<MetadataIdentityConflictException>(() =>
            repository.UpdateMetadataAsync(held.Id, proposed, CancellationToken.None));
        Assert.Equal("Held Movie", (await repository.GetByIdAsync(held.Id, CancellationToken.None))!.Title);
    }

    [Fact]
    public async Task Series_remap_conflict_prevents_a_second_show_from_claiming_a_held_provider_identity()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-01T05:00:00Z"));
        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SeriesRepository(storage.Factory, clock);
        var current = await repository.AddAsync(
            new CreateSeriesRequest("Current Show", 2020, "tt0000011", MetadataProvider: "tmdb", MetadataProviderId: "11"),
            CancellationToken.None);
        var held = await repository.AddAsync(
            new CreateSeriesRequest("Held Show", 2021, "tt0000012", MetadataProvider: "tmdb", MetadataProviderId: "12"),
            CancellationToken.None);

        var conflict = await repository.FindMetadataIdentityConflictAsync(
            current.Id, held.Title, held.StartYear, held.ImdbId, "tmdb", "12", CancellationToken.None);

        Assert.NotNull(conflict);
        Assert.Equal(held.Id, conflict.Id);
        Assert.Equal("provider-id", conflict.Reason);

        var proposed = Metadata("199", "One Show Identity", "tv", 2025, "tt0000199");
        Assert.NotNull(await repository.UpdateMetadataAsync(current.Id, proposed, CancellationToken.None));
        await Assert.ThrowsAsync<MetadataIdentityConflictException>(() =>
            repository.UpdateMetadataAsync(held.Id, proposed, CancellationToken.None));
        Assert.Equal("Held Show", (await repository.GetByIdAsync(held.Id, CancellationToken.None))!.Title);
    }

    [Fact]
    public void Preview_token_changes_when_title_state_provider_identity_or_episode_catalogue_changes()
    {
        var identity = new MetadataLinkIdentity("tmdb", "42", "Proposed", 2020, "tt0000042", "Network");
        var timestamp = DateTimeOffset.Parse("2026-09-01T05:00:00Z");
        var first = MetadataLinkPreviewTokens.Create("subject", timestamp, identity, ["S0001E0001"]);

        Assert.Equal(first, MetadataLinkPreviewTokens.Create("subject", timestamp, identity, ["S0001E0001"]));
        Assert.NotEqual(first, MetadataLinkPreviewTokens.Create("subject", timestamp.AddSeconds(1), identity, ["S0001E0001"]));
        Assert.NotEqual(first, MetadataLinkPreviewTokens.Create("subject", timestamp, identity with { ProviderId = "43" }, ["S0001E0001"]));
        Assert.NotEqual(first, MetadataLinkPreviewTokens.Create("subject", timestamp, identity, ["S0001E0001", "S0001E0002"]));
    }

    [Fact]
    public void Series_catalogue_remap_blocks_missing_existing_episodes_and_allows_a_true_superset()
    {
        MetadataEpisodeIdentity[] existing =
        [
            new(1, 1, HasFile: true),
            new(1, 2)
        ];

        var mismatch = MetadataCatalogueSafety.Evaluate(
            existing,
            [new(1, 2), new(1, 3)]);
        var superset = MetadataCatalogueSafety.Evaluate(
            existing,
            [new(1, 1), new(1, 2), new(1, 3), new(2, 1)]);

        Assert.False(mismatch.PreservesExistingCatalogue);
        Assert.Equal(1, mismatch.Impact.ExistingEpisodesOutsideProposed);
        Assert.Equal(1, mismatch.Impact.ImportedEpisodeCount);
        Assert.Equal(1, mismatch.NewEpisodeCount);

        Assert.True(superset.PreservesExistingCatalogue);
        Assert.Equal(0, superset.Impact.ExistingEpisodesOutsideProposed);
        Assert.Equal(2, superset.Impact.ProposedSeasonCount);
        Assert.Equal(2, superset.NewEpisodeCount);
        Assert.Equal(["S0001E0001", "S0001E0002", "S0001E0003", "S0002E0001"], superset.ProposedKeys);
    }

    private static MetadataSearchResult Metadata(string id, string title, string mediaType, int year, string imdbId)
        => new("tmdb", id, mediaType, title, null, year, "overview", null, null, 7, [], [], imdbId, null);
}
