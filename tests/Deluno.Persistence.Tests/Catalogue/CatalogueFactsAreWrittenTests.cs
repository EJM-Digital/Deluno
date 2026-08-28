using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.Metadata;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Catalogue;

/// <summary>
/// A filter over an empty column returns no rows and looks like a fair answer.
///
/// <para>This is the defect this codebase keeps producing, and every instance
/// has looked the same: a column is added, a control is declared over it, and
/// nothing ever writes it. <c>network</c> had no writer for four schema
/// versions. <c>status</c> survived four separate attempts at being persisted,
/// each of which passed its own test. Nobody sees an error — the shelf simply
/// says there are no A24 films, and it is wrong.</para>
///
/// <para>So the guard is not "does the write compile" but <b>does a provider
/// answer reach the column a filter reads</b>, through the real path, for every
/// fact #306 and #319 added. If a future field is added to
/// <see cref="MetadataSearchResult"/> and not mapped in
/// <c>CatalogueMetadata</c>, this is what says so.</para>
/// </summary>
public sealed class CatalogueFactsAreWrittenTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-29T00:00:00Z");

    /// <summary>A provider answering with everything it can answer with.</summary>
    private static MetadataSearchResult Answer(string mediaType) => new(
        Provider: "tmdb",
        ProviderId: "157336",
        MediaType: mediaType,
        Title: "Interstellar",
        OriginalTitle: "Interstellar",
        Year: 2014,
        Overview: "They went looking for a new home.",
        PosterUrl: "/api/metadata/artwork/poster",
        BackdropUrl: "/api/metadata/artwork/backdrop",
        Rating: 8.4,
        Ratings:
        [
            new Deluno.Integrations.Metadata.MetadataRatingItem("tmdb", "TMDb", 8.4, 10, 36_000, null, "community"),
            new Deluno.Integrations.Metadata.MetadataRatingItem("imdb", "IMDb", 8.7, 10, 2_100_000, null, "community"),
            new Deluno.Integrations.Metadata.MetadataRatingItem("rotten_tomatoes", "Rotten Tomatoes", 73, 100, null, null, "critic"),
            new Deluno.Integrations.Metadata.MetadataRatingItem("metacritic", "Metacritic", 74, 100, null, null, "critic"),
            // A source Deluno keeps no column for. It must be ignored rather
            // than dropped into whichever column happens to be next.
            new Deluno.Integrations.Metadata.MetadataRatingItem("trakt", "Trakt", 9.1, 10, 40_000, null, "community")
        ],
        Genres: ["Adventure", "Drama"],
        ImdbId: "tt0816692",
        ExternalUrl: "https://www.themoviedb.org/movie/157336",
        Certification: "PG-13",
        Studio: "Legendary Pictures",
        Network: "HBO",
        Collection: "The Nolan Collection",
        OriginalLanguage: "en",
        Status: mediaType == "movies" ? "Released" : "Ended",
        RuntimeMinutes: 169,
        Popularity: 91.2,
        VoteCount: 36_000);

    [Fact]
    public async Task Every_fact_a_film_can_be_filtered_by_survives_the_write()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateMoviesAsync(storage);

        var movie = await movies.AddAsync(
            new CreateMovieRequest("Interstellar", 2014, "tt0816692"),
            CancellationToken.None);

        await movies.UpdateMetadataAsync(movie.Id, Answer("movies"), CancellationToken.None);

        // Read through the filters, not the row: a value in a column a filter
        // does not reach is the same as no value at all.
        await AssertFiltersAsync(
            filters => movies.ListPageAsync(
                new CatalogueQuery(Filters: filters), CancellationToken.None)
                .ContinueWith(page => page.Result.Items.Count, TaskScheduler.Default),
            MediaKind.Movie,
            [
                "certification:is:PG-13",
                "originalLanguage:is:en",
                "studio:is:Legendary Pictures",
                "collection:is:The Nolan Collection",
                "movieStatus:in:Released"
            ]);
    }

    [Fact]
    public async Task Every_fact_a_show_can_be_filtered_by_survives_the_write()
    {
        using var storage = TestStorage.Create();
        var series = await CreateSeriesAsync(storage);

        var show = await series.AddAsync(
            new CreateSeriesRequest("Interstellar", 2014, "tt0816692"),
            CancellationToken.None);

        await series.UpdateMetadataAsync(show.Id, Answer("tv"), CancellationToken.None);

        await AssertFiltersAsync(
            filters => series.ListPageAsync(
                new CatalogueQuery(Filters: filters), CancellationToken.None)
                .ContinueWith(page => page.Result.Items.Count, TaskScheduler.Default),
            MediaKind.Series,
            [
                "certification:is:PG-13",
                "originalLanguage:is:en",
                // A show's "who made it" is the network, not the studio.
                "network:is:HBO",
                "seriesStatus:in:Ended"
            ]);
    }

    /// <summary>
    /// The four scores land in four columns, each one filterable on its own —
    /// and a source Deluno keeps no column for is left in the blob rather than
    /// written somewhere convenient.
    /// </summary>
    [Fact]
    public async Task The_four_scores_are_filterable_separately_and_a_fifth_source_is_not_invented()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateMoviesAsync(storage);

        var movie = await movies.AddAsync(
            new CreateMovieRequest("Interstellar", 2014, "tt0816692"),
            CancellationToken.None);

        await movies.UpdateMetadataAsync(movie.Id, Answer("movies"), CancellationToken.None);

        // Each score is above its own floor and below the next one up, so a
        // column holding the wrong source's number fails rather than passing by
        // luck: 8.4, 8.7, 73 and 74 are four distinguishable values.
        await AssertFiltersAsync(
            filters => movies.ListPageAsync(
                new CatalogueQuery(Filters: filters), CancellationToken.None)
                .ContinueWith(page => page.Result.Items.Count, TaskScheduler.Default),
            MediaKind.Movie,
            [
                "ratingtmdb:min:8.4",
                "ratingimdb:min:8.7",
                "ratingrottentomatoes:min:73",
                "ratingmetacritic:min:74",
                "votestmdb:min:36000",
                "votesimdb:min:2100000"
            ]);

        // The distinguishing half: TMDb is 8.4, so a demand for 8.7 must fail.
        // Without this the assertions above would also pass if every column held
        // the same number.
        var confused = CatalogueFilters.Parse(MediaKind.Movie, ["ratingtmdb:min:8.7"], out _);
        var page = await movies.ListPageAsync(new CatalogueQuery(Filters: confused), CancellationToken.None);
        Assert.Empty(page.Items);

        // Trakt was answered for and Deluno has no column for it, so it is not
        // a filter at all — as opposed to a filter reading someone else's score.
        Assert.DoesNotContain(
            CatalogueFilterFields.For(MediaKind.Movie),
            field => field.Id.Contains("trakt", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Every filter must select the one row that was written. A filter that
    /// matches nothing is the failure; a filter the registry refuses is a
    /// worse one, because it means the field was never declared.
    /// </summary>
    private static async Task AssertFiltersAsync(
        Func<CatalogueFilters, Task<int>> count,
        MediaKind kind,
        string[] conditions)
    {
        foreach (var condition in conditions)
        {
            var filters = CatalogueFilters.Parse(kind, [condition], out var errors);

            Assert.True(errors.Count == 0, $"{condition} was refused: {string.Join("; ", errors)}");
            Assert.False(filters.IsEmpty, $"{condition} parsed to nothing.");
            Assert.True(await count(filters) == 1, $"{condition} matched no rows, so nothing wrote its column.");
        }
    }

    private static async Task<SqliteMovieCatalogRepository> CreateMoviesAsync(TestStorage storage)
    {
        var clock = new FixedTimeProvider(Now);
        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return new SqliteMovieCatalogRepository(storage.Factory, clock);
    }

    private static async Task<SqliteSeriesCatalogRepository> CreateSeriesAsync(TestStorage storage)
    {
        var clock = new FixedTimeProvider(Now);
        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return new SqliteSeriesCatalogRepository(storage.Factory, clock);
    }
}
