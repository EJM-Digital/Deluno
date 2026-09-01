using Deluno.Integrations.Metadata;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Movies;

public sealed class MovieCollectionPersistenceTests
{
    [Fact]
    public async Task Stores_full_membership_counts_missing_titles_and_exclusions()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-31T00:00:00Z"));
        await new MoviesSchemaInitializer(
            storage.Factory,
            new Deluno.Infrastructure.Storage.Migrations.SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var collections = new SqliteMovieCollectionsRepository(storage.Factory, timeProvider);
        var held = await movies.AddAsync(
            new CreateMovieRequest("Held film", 2010, null, MetadataProvider: "tmdb", MetadataProviderId: "101"),
            CancellationToken.None);

        var metadata = new MetadataCollection(
            "tmdb",
            "645",
            "The Nolan Collection",
            "A franchise.",
            "https://image/collection.jpg",
            null,
            [
                new MetadataCollectionMovie("101", "Held film", 2010, "Held", "https://image/101.jpg", null, "https://tmdb/movie/101"),
                new MetadataCollectionMovie("202", "Missing film", 2014, "Missing", "https://image/202.jpg", null, "https://tmdb/movie/202")
            ]);

        var collection = await collections.UpsertAsync(
            "movies-main",
            "Movies",
            "D:\\Movies",
            "quality-1",
            "HD",
            new CreateMovieCollectionRequest("645", "movies-main", Monitored: true),
            metadata,
            CancellationToken.None);
        await collections.SaveSnapshotAsync(collection.Id, metadata, timeProvider.GetUtcNow(), CancellationToken.None);

        var listed = Assert.Single(await collections.ListAsync(CancellationToken.None));
        Assert.Equal(2, listed.MemberCount);
        Assert.Equal(1, listed.HeldCount);
        Assert.Equal(1, listed.MissingCount);

        var members = await collections.ListMembersAsync(collection.Id, CancellationToken.None);
        Assert.Equal(held.Id, Assert.Single(members, item => item.ProviderId == "101").LocalMovieId);
        Assert.Null(Assert.Single(members, item => item.ProviderId == "202").LocalMovieId);

        Assert.True(await collections.SetMemberExcludedAsync(collection.Id, "202", true, CancellationToken.None));
        Assert.True(Assert.Single(await collections.ListMembersAsync(collection.Id, CancellationToken.None), item => item.ProviderId == "202").IsExcluded);

        var due = await collections.ClaimDueAsync(timeProvider.GetUtcNow(), TimeSpan.FromHours(24), CancellationToken.None);
        Assert.Single(due);
        Assert.NotNull(due[0].NextSyncUtc);
        Assert.Empty(await collections.ClaimDueAsync(timeProvider.GetUtcNow(), TimeSpan.FromHours(24), CancellationToken.None));
    }
}
