using Deluno.Media;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Movies;

/// <summary>
/// The narrowing a shelf can be asked for beyond its status: quality, size,
/// genre, year, runtime, rating.
///
/// The browser used to own this and it was deleted in #302, because it could
/// express filters nothing could answer — two of its branches tested values
/// nothing ever set and so matched zero rows forever, silently. These are the
/// replacement, and every one of them is a real column read in SQL.
///
/// The one that matters most is the last: **the counts above the shelf and the
/// rows on it have to be the same set.** They are computed by two different
/// queries, which is exactly the shape that drifts.
/// </summary>
public sealed class CatalogueCustomFilterTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-27T00:00:00Z");

    [Fact]
    public async Task Quality_selects_the_tier_and_not_the_resolution()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateAsync(storage);

        await ImportAsync(movies, "Dune", 2021, "Remux 2160p", 60);
        await ImportAsync(movies, "Arrival", 2016, "WEB 2160p", 8);
        await ImportAsync(movies, "Inception", 2010, "WEB 1080p", 3);

        // The whole point of James's ask: these two are both "4K" and they are
        // not the same file.
        var remux = await ListAsync(movies, Filter("quality", CatalogueFilterOperator.Includes, "Remux 2160p"));
        Assert.Equal(["Dune"], remux);

        var anyFourK = await ListAsync(movies, Filter("quality", CatalogueFilterOperator.Includes, "Remux 2160p", "WEB 2160p"));
        Assert.Equal(["Arrival", "Dune"], anyFourK.Order());
    }

    [Fact]
    public async Task A_title_with_no_file_matches_no_quality_and_no_size()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateAsync(storage);

        await ImportAsync(movies, "Dune", 2021, "Remux 2160p", 60);
        await movies.AddAsync(new CreateMovieRequest("Sicario", 2015, null), CancellationToken.None);

        // Asking for "under 100 GB" and being handed titles that have no file at
        // all is the same class of answer as a badge showing a target quality as
        // if it were owned.
        var underAHundred = await ListAsync(movies, Filter("size", CatalogueFilterOperator.AtMost, "100"));
        Assert.Equal(["Dune"], underAHundred);

        var anyQuality = await ListAsync(movies, Filter("quality", CatalogueFilterOperator.Includes, "Remux 2160p", "WEB 1080p"));
        Assert.Equal(["Dune"], anyQuality);
    }

    [Fact]
    public async Task Size_is_a_range_over_the_file_on_disk()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateAsync(storage);

        await ImportAsync(movies, "Big", 2021, "Remux 2160p", 60);
        await ImportAsync(movies, "Middling", 2016, "WEB 2160p", 8);
        await ImportAsync(movies, "Small", 2010, "WEB 1080p", 2);

        Assert.Equal(["Middling"], await ListAsync(movies, Filter("size", CatalogueFilterOperator.AtLeast, "5").And("size", CatalogueFilterOperator.AtMost, "20")));
        Assert.Equal(["Big"], await ListAsync(movies, Filter("size", CatalogueFilterOperator.AtLeast, "40")));
    }

    [Fact]
    public async Task Two_genres_means_both_and_a_whole_genre_at_that()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateAsync(storage);

        var both = await movies.AddAsync(new CreateMovieRequest("Arrival", 2016, null), CancellationToken.None);
        await SetGenresAsync(movies, both.Id, "Drama, Science Fiction");
        var one = await movies.AddAsync(new CreateMovieRequest("Sicario", 2015, null), CancellationToken.None);
        await SetGenresAsync(movies, one.Id, "Drama, Thriller");
        // The reason a genre match is bracketed in commas rather than a bare
        // LIKE: this is not a Drama.
        var neither = await movies.AddAsync(new CreateMovieRequest("Douze", 1999, null), CancellationToken.None);
        await SetGenresAsync(movies, neither.Id, "Melodrama");

        Assert.Equal(["Arrival", "Sicario"], (await ListAsync(movies, Filter("genre", CatalogueFilterOperator.IncludesAll, "Drama"))).Order());
        Assert.Equal(["Arrival"], await ListAsync(movies, Filter("genre", CatalogueFilterOperator.IncludesAll, "Drama", "Science Fiction")));
    }

    [Fact]
    public async Task Tags_match_as_exact_user_labels_and_support_any_all_and_none()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateAsync(storage);
        var tags = new SqliteMediaTagStore(storage.Factory, new FixedTimeProvider(Now));

        var tagged = await movies.AddAsync(new CreateMovieRequest("Tagged", 2024, null), CancellationToken.None);
        await tags.ReplaceAsync(
            MediaKind.Movie,
            tagged.Id,
            [
                new MediaTagAssignment("tag-4k", "4K rewatch"),
                new MediaTagAssignment("tag-kids", "kids")
            ],
            CancellationToken.None);

        await movies.AddAsync(new CreateMovieRequest("Untagged", 2023, null), CancellationToken.None);

        Assert.Equal(["Tagged"], await ListAsync(movies, Filter("tag", CatalogueFilterOperator.Includes, "4K rewatch")));
        Assert.Equal(["Tagged"], await ListAsync(movies, Filter("tag", CatalogueFilterOperator.Includes, "4K rewatch", "kids")));
        Assert.Equal(["Tagged"], await ListAsync(movies, Filter("tag", CatalogueFilterOperator.IncludesAll, "4K rewatch", "kids")));
        Assert.Equal(["Untagged"], await ListAsync(movies, Filter("tag", CatalogueFilterOperator.Excludes, "kids")));

        // Exact membership is important: the label "4K" must not match the
        // distinct user label "4K rewatch".
        Assert.Empty(await ListAsync(movies, Filter("tag", CatalogueFilterOperator.Includes, "4K")));
    }

    [Fact]
    public async Task Year_runtime_and_rating_narrow_on_the_title_itself()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateAsync(storage);

        var old = await movies.AddAsync(new CreateMovieRequest("Old", 1994, null), CancellationToken.None);
        await SetFactsAsync(movies, old.Id, runtimeMinutes: 90, rating: 6.0);
        var recent = await movies.AddAsync(new CreateMovieRequest("Recent", 2021, null), CancellationToken.None);
        await SetFactsAsync(movies, recent.Id, runtimeMinutes: 155, rating: 8.4);

        Assert.Equal(["Recent"], await ListAsync(movies, Filter("year", CatalogueFilterOperator.AtLeast, "2000")));
        Assert.Equal(["Old"], await ListAsync(movies, Filter("runtime", CatalogueFilterOperator.AtMost, "120")));
        Assert.Equal(["Recent"], await ListAsync(movies, Filter("rating", CatalogueFilterOperator.AtLeast, "8.0")));
    }

    /// <summary>
    /// The counts above the shelf and the rows on it are two different queries,
    /// which is the shape that drifts. A filtered page whose chip says 3 and
    /// whose grid shows 1 is the same defect as the sidebar and the dashboard
    /// counting "needs you" from two sources.
    /// </summary>
    [Fact]
    public async Task The_counts_above_the_shelf_count_the_rows_on_it()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateAsync(storage);

        await ImportAsync(movies, "Dune", 2021, "Remux 2160p", 60);
        await ImportAsync(movies, "Arrival", 2016, "WEB 2160p", 8);
        await ImportAsync(movies, "Inception", 2010, "WEB 1080p", 3);
        await movies.AddAsync(new CreateMovieRequest("Sicario", 2015, null), CancellationToken.None);

        var page = await movies.ListPageAsync(
            new CatalogueQuery(Filters: Filter("quality", CatalogueFilterOperator.Includes, "WEB 2160p", "WEB 1080p")),
            CancellationToken.None);

        Assert.Equal(2, page.Items.Count);
        Assert.NotNull(page.Facets);
        Assert.Equal(2, page.Facets!.All);
        Assert.Equal(page.Items.Count, page.TotalCount);
    }

    [Fact]
    public async Task An_unfiltered_page_is_untouched()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateAsync(storage);

        await ImportAsync(movies, "Dune", 2021, "Remux 2160p", 60);
        await movies.AddAsync(new CreateMovieRequest("Sicario", 2015, null), CancellationToken.None);

        // CatalogueFilters.None must be indistinguishable from no filters at
        // all — the rule that keeps this free for anybody not using it.
        var none = await movies.ListPageAsync(new CatalogueQuery(Filters: CatalogueFilters.None), CancellationToken.None);
        var absent = await movies.ListPageAsync(new CatalogueQuery(), CancellationToken.None);

        Assert.Equal(2, none.Items.Count);
        Assert.Equal(absent.Items.Count, none.Items.Count);
        Assert.Equal(absent.Facets!.All, none.Facets!.All);
    }

    /* ------------------------------------------------------------ helpers */

    /// <summary>
    /// One condition, spelled the way the panel above the shelf reads. The
    /// filters were nine fixed properties on a record until #324; they are a
    /// list of conditions over a server-declared field registry now, so a test
    /// names a field the same way a URL does.
    /// </summary>
    private static CatalogueFilters Filter(string fieldId, CatalogueFilterOperator op, params string[] values)
        => CatalogueFilters.Of(CatalogueFilters.Where(fieldId, op, values));

    private static async Task<string[]> ListAsync(IMovieCatalogRepository movies, CatalogueFilters filters)
    {
        var page = await movies.ListPageAsync(new CatalogueQuery(Filters: filters), CancellationToken.None);
        return page.Items.Select(item => item.Title).ToArray();
    }

    private static Task ImportAsync(IMovieCatalogRepository movies, string title, int year, string quality, double sizeGb)
        => movies.ImportExistingBatchAsync(
            "library-movies",
            [
                new ExistingMovieImportRequest(
                    Title: title,
                    ReleaseYear: year,
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

    private static Task SetGenresAsync(IMovieCatalogRepository movies, string id, string genres)
        => movies.UpdateMetadataAsync(
                new MediaMetadataUpdate(
                    id,
                    "tmdb",
                    id,
                    null,
                    null,
                    null,
                    null,
                    null,
                    genres,
                    null,
                    null,
                    "{}"),
                CancellationToken.None);

    private static Task SetFactsAsync(IMovieCatalogRepository movies, string id, int runtimeMinutes, double rating)
        => movies.UpdateMetadataAsync(
                new MediaMetadataUpdate(
                    id,
                    "tmdb",
                    id,
                    null,
                    null,
                    null,
                    null,
                    rating,
                    null,
                    null,
                    null,
                    "{}",
                    RuntimeMinutes: runtimeMinutes),
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
