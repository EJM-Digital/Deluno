using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Movies;

/// <summary>
/// Sorting a shelf by the file: how big it is and how good it is.
///
/// Media is made of files, so a library has to be orderable by them. The facts
/// live on the wanted state, which the page reaches through a correlated pick
/// SQLite cannot index, so V0016 keeps the picked file's size and quality rank
/// on the entry and a trigger keeps them true.
///
/// The last test here is the one that matters most. The pick order now exists
/// twice — once in C# in <c>CatalogueWantedState.Join</c> and once in SQL in
/// the trigger — and if they ever disagreed a page would sort by one file's
/// size while displaying another's. That is the defect this codebase keeps
/// paying for, so it is pinned rather than trusted.
/// </summary>
public sealed class CatalogueSortableFactsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-27T00:00:00Z");

    [Fact]
    public async Task A_shelf_sorts_by_the_size_of_the_file()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateAsync(storage);

        await ImportAsync(movies, "Small", "WEB 1080p", 2);
        await ImportAsync(movies, "Huge", "Remux 2160p", 60);
        await ImportAsync(movies, "Middling", "WEB 2160p", 8);

        Assert.Equal(
            ["Huge", "Middling", "Small"],
            await TitlesAsync(movies, CatalogueSortFields.Size, descending: true));

        Assert.Equal(
            ["Small", "Middling", "Huge"],
            await TitlesAsync(movies, CatalogueSortFields.Size, descending: false));
    }

    [Fact]
    public async Task A_shelf_sorts_by_the_quality_ladder_rather_than_the_alphabet()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateAsync(storage);

        await ImportAsync(movies, "Web4k", "WEB 2160p", 8);
        await ImportAsync(movies, "Remux4k", "Remux 2160p", 60);
        await ImportAsync(movies, "Web1080", "WEB 1080p", 3);

        // Alphabetically this would be Remux, WEB, WEB. By the ladder, a Remux
        // 2160p outranks a WEB 2160p outranks a WEB 1080p, which is the whole
        // reason the ladder exists.
        Assert.Equal(
            ["Remux4k", "Web4k", "Web1080"],
            await TitlesAsync(movies, CatalogueSortFields.Quality, descending: true));
    }

    [Fact]
    public async Task A_title_with_no_file_sorts_below_every_title_that_has_one()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateAsync(storage);

        await ImportAsync(movies, "Held", "WEB 1080p", 3);
        await movies.AddAsync(new CreateMovieRequest("Missing", 2015, null), CancellationToken.None);

        // It has no size, so it cannot be ranked among sizes. Last is the only
        // honest place for it, and it must not read as zero bytes of nothing.
        Assert.Equal(["Held", "Missing"], await TitlesAsync(movies, CatalogueSortFields.Size, descending: true));
    }

    [Fact]
    public async Task Replacing_the_file_re_sorts_the_shelf()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateAsync(storage);

        await ImportAsync(movies, "Arrival", "WEB 1080p", 3);
        await ImportAsync(movies, "Dune", "WEB 2160p", 8);
        Assert.Equal(["Dune", "Arrival"], await TitlesAsync(movies, CatalogueSortFields.Size, descending: true));

        // The trigger is the whole point: nothing in the import path knows the
        // cached columns exist, and the order still follows the upgrade.
        await ImportAsync(movies, "Arrival", "Remux 2160p", 60);
        Assert.Equal(["Arrival", "Dune"], await TitlesAsync(movies, CatalogueSortFields.Size, descending: true));
        Assert.Equal(["Arrival", "Dune"], await TitlesAsync(movies, CatalogueSortFields.Quality, descending: true));
    }

    /// <summary>
    /// The cached facts must describe the same wanted-state row the page reads
    /// its quality and size from. Two copies of the pick rule, held together.
    /// </summary>
    [Fact]
    public async Task The_cached_facts_describe_the_row_the_page_displays()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateAsync(storage);

        var film = await movies.AddAsync(new CreateMovieRequest("Arrival", 2016, null), CancellationToken.None);

        // Held in two libraries with two different files. The page picks the one
        // that has a file and is still short of its cutoff; the trigger must
        // pick the same one.
        await movies.EnsureWantedStateAsync(film.Id, "library-4k", "covered", "Meets the 4K profile.", true, "Remux 2160p", "Remux 2160p", true, CancellationToken.None);
        await movies.EnsureWantedStateAsync(film.Id, "library-hd", "upgrade", "Short of the HD profile.", true, "WEB 1080p", "Bluray 1080p", false, CancellationToken.None);

        var page = await movies.ListPageAsync(new CatalogueQuery(), CancellationToken.None);
        var item = Assert.Single(page.Items);

        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Movies);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT primary_quality_rank FROM movie_entries WHERE id = @id;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = film.Id;
        command.Parameters.Add(parameter);
        var cachedRank = Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken.None));

        // The page displays the HD copy, because it is the one still short of
        // its cutoff. So the sortable rank has to be the HD copy's, not the 4K
        // one's — otherwise the shelf would sort a title above where it reads.
        Assert.Equal("WEB 1080p", item.CurrentQuality);
        Assert.Equal(70, cachedRank);
    }

    /// <summary>
    /// The whole reason the cached columns exist: these two orders have to be an
    /// index walk, not a scan of the catalogue.
    ///
    /// This is the assertion that would have failed if the sort had been wired
    /// straight to the wanted state, and it is the one nothing about the result
    /// would ever have shown — only the twenty-thousandth title, on a machine
    /// nobody tests on.
    /// </summary>
    [Theory]
    [InlineData(CatalogueSortFields.Size)]
    [InlineData(CatalogueSortFields.Quality)]
    [InlineData(CatalogueSortFields.Bitrate)]
    public async Task Sorting_by_the_file_stays_an_index_walk(string sort)
    {
        using var storage = TestStorage.Create();
        await CreateAsync(storage);

        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Movies);
        using var command = connection.CreateCommand();
        var sortExpression = CatalogueKeyset.SortExpression(sort, "m", "release_year");
        command.CommandText =
            $"""
            EXPLAIN QUERY PLAN
            SELECT m.id
            FROM movie_entries m
            ORDER BY {CatalogueKeyset.OrderBy(sortExpression, "m", descending: true)}
            LIMIT 51;
            """;

        var plan = new List<string>();
        using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            plan.Add(reader.GetString(3));
        }

        Assert.NotEmpty(plan);
        // A temp B-tree here means SQLite sorted the whole catalogue before it
        // could return fifty rows.
        Assert.All(plan, line => Assert.DoesNotContain("TEMP B-TREE", line));
        // SQLite reports the walk as "SCAN … USING COVERING INDEX ix_movie_entries_size_id":
        // it reads the index in order and stops at the LIMIT, never touching the
        // table. "SCAN" there is the index being read in order, not the table.
        Assert.Contains(plan, line => line.Contains("USING COVERING INDEX", StringComparison.Ordinal));
    }

    /* ------------------------------------------------------------ helpers */

    private static async Task<string[]> TitlesAsync(IMovieCatalogRepository movies, string sort, bool descending)
    {
        var page = await movies.ListPageAsync(
            new CatalogueQuery(Sort: sort, Descending: descending),
            CancellationToken.None);
        return page.Items.Select(item => item.Title).ToArray();
    }

    private static Task ImportAsync(IMovieCatalogRepository movies, string title, string quality, double sizeGb)
        => movies.ImportExistingBatchAsync(
            "library-movies",
            [
                new ExistingMovieImportRequest(
                    Title: title,
                    ReleaseYear: 2016,
                    WantedStatus: WantedStatuses.Covered,
                    WantedReason: "Imported from your existing library.",
                    CurrentQuality: quality,
                    TargetQuality: quality,
                    QualityCutoffMet: true,
                    UnmonitorWhenCutoffMet: false,
                    FilePath: $@"D:\Media\{title}\{title}.mkv",
                    FileSizeBytes: (long)(sizeGb * 1024 * 1024 * 1024))
            ],
            CancellationToken.None);

    private static async Task<SqliteMovieCatalogRepository> CreateAsync(TestStorage storage)
    {
        var timeProvider = new FixedTimeProvider(Now);
        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
    }
}
