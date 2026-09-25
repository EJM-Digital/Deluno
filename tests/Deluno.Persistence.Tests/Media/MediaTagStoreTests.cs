using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Infrastructure.Storage;
using Deluno.Media;
using Deluno.Media.Migrations;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Movies.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Series.Migrations;
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

    [Fact]
    public async Task Bulk_add_mode_keeps_existing_tags_and_merges_new_ones()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Now);
        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var movies = new SqliteMovieCatalogRepository(storage.Factory, clock);
        var store = new SqliteMediaTagStore(storage.Factory, clock);
        var movie = await movies.AddAsync(new CreateMovieRequest("Bulk Add", 2024, null), CancellationToken.None);
        await store.ReplaceAsync(MediaKind.Movie, movie.Id, [new MediaTagAssignment("kept", "Kept")], CancellationToken.None);

        var current = await store.ListAsync(MediaKind.Movie, movie.Id, CancellationToken.None);
        var requested = new[] { new MediaTagAssignment("added", "Added") };
        var merged = MediaTagBulkOperations.Apply(BulkTagMode.Add, current, requested);
        await store.ReplaceAsync(MediaKind.Movie, movie.Id, merged, CancellationToken.None);

        var result = await store.ListAsync(MediaKind.Movie, movie.Id, CancellationToken.None);
        Assert.Equal(["Added", "Kept"], result.Select(item => item.Name).OrderBy(name => name));
    }

    [Fact]
    public async Task Bulk_remove_mode_only_removes_the_named_tags()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Now);
        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var series = new SqliteSeriesCatalogRepository(storage.Factory, clock);
        var store = new SqliteMediaTagStore(storage.Factory, clock);
        var seriesItem = await series.AddAsync(new CreateSeriesRequest("Bulk Remove", 2024, null), CancellationToken.None);
        await store.ReplaceAsync(
            MediaKind.Series,
            seriesItem.Id,
            [new MediaTagAssignment("keep", "Keep"), new MediaTagAssignment("drop", "Drop")],
            CancellationToken.None);

        var current = await store.ListAsync(MediaKind.Series, seriesItem.Id, CancellationToken.None);
        var requested = new[] { new MediaTagAssignment("drop", "Drop") };
        var merged = MediaTagBulkOperations.Apply(BulkTagMode.Remove, current, requested);
        await store.ReplaceAsync(MediaKind.Series, seriesItem.Id, merged, CancellationToken.None);

        var result = await store.ListAsync(MediaKind.Series, seriesItem.Id, CancellationToken.None);
        Assert.Equal(["Keep"], result.Select(item => item.Name));
    }

    [Fact]
    public async Task Bulk_replace_mode_behaves_like_today_and_wipes_other_tags()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Now);
        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var series = new SqliteSeriesCatalogRepository(storage.Factory, clock);
        var store = new SqliteMediaTagStore(storage.Factory, clock);
        var seriesItem = await series.AddAsync(new CreateSeriesRequest("Bulk Replace", 2024, null), CancellationToken.None);
        await store.ReplaceAsync(MediaKind.Series, seriesItem.Id, [new MediaTagAssignment("old", "Old")], CancellationToken.None);

        var current = await store.ListAsync(MediaKind.Series, seriesItem.Id, CancellationToken.None);
        var requested = new[] { new MediaTagAssignment("new", "New") };
        var merged = MediaTagBulkOperations.Apply(BulkTagMode.Replace, current, requested);
        await store.ReplaceAsync(MediaKind.Series, seriesItem.Id, merged, CancellationToken.None);

        var result = await store.ListAsync(MediaKind.Series, seriesItem.Id, CancellationToken.None);
        Assert.Equal(["New"], result.Select(item => item.Name));
    }

    [Theory]
    [InlineData(null, true, BulkTagMode.Add)]
    [InlineData("", true, BulkTagMode.Add)]
    [InlineData("add", true, BulkTagMode.Add)]
    [InlineData("ADD", true, BulkTagMode.Add)]
    [InlineData("remove", true, BulkTagMode.Remove)]
    [InlineData("replace", true, BulkTagMode.Replace)]
    [InlineData("wipe", false, BulkTagMode.Add)]
    public void TryParseMode_defaults_to_add_and_rejects_unknown_values(string? raw, bool expectedSuccess, BulkTagMode expectedMode)
    {
        var success = MediaTagBulkOperations.TryParseMode(raw, out var mode);

        Assert.Equal(expectedSuccess, success);
        if (expectedSuccess)
        {
            Assert.Equal(expectedMode, mode);
        }
    }
}
