using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Infrastructure.Storage;
using Deluno.Media;
using Deluno.Media.Migrations;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Movies.Migrations;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Media;

public sealed class MediaTagStoreTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-27T00:00:00Z");

    [Fact]
    public async Task Assignments_are_exact_and_usage_is_counted_per_title()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Now);
        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, clock);
        var store = new SqliteMediaTagStore(storage.Factory, clock);
        var first = await movies.AddAsync(new CreateMovieRequest("First", 2024, null), CancellationToken.None);
        var second = await movies.AddAsync(new CreateMovieRequest("Second", 2023, null), CancellationToken.None);

        await store.ReplaceAsync(MediaKind.Movie, first.Id, [new MediaTagAssignment("managed", "4K rewatch")], CancellationToken.None);
        await store.ReplaceAsync(MediaKind.Movie, second.Id, [new MediaTagAssignment("other", "4K")], CancellationToken.None);

        Assert.Equal(["4K rewatch"], (await store.ListAsync(MediaKind.Movie, first.Id, CancellationToken.None)).Select(item => item.Name));
        Assert.Equal(1, Assert.Single(await store.ListUsageAsync(MediaKind.Movie, CancellationToken.None), item => item.Name == "4K rewatch").TitleCount);

        var page = await movies.ListPageAsync(
            new CatalogueQuery(Filters: CatalogueFilters.Of(CatalogueFilters.Where(
                "tag", CatalogueFilterOperator.Includes, "4K rewatch"))),
            CancellationToken.None);
        Assert.Equal(["First"], page.Items.Select(item => item.Title));
    }

    [Fact]
    public async Task Legacy_metadata_labels_are_migrated_into_the_join()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Now);
        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var movie = new SqliteMovieCatalogRepository(storage.Factory, clock);
        var created = await movie.AddAsync(new CreateMovieRequest(
            "Legacy",
            2022,
            null,
            MetadataJson: "{\"Tags\":[\"Kids\"]}"), CancellationToken.None);
        await using (var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Movies, CancellationToken.None))
        await using (var transaction = await connection.BeginTransactionAsync(CancellationToken.None))
        {
            await new MediaTagsMigration(MediaKind.Movie, 31).UpAsync(connection, transaction, CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }
        var store = new SqliteMediaTagStore(storage.Factory, clock);

        var assignment = Assert.Single(await store.ListAsync(MediaKind.Movie, created.Id, CancellationToken.None));
        Assert.Equal("Kids", assignment.Name);
        Assert.StartsWith("legacy:", assignment.TagId, StringComparison.Ordinal);
    }
}
