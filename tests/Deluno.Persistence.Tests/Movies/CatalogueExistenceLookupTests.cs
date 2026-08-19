using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Movies;

/// <summary>
/// "Do I already have this title?" has to be an indexed lookup, not a scan.
/// Intake asked it by loading the whole catalogue into a dictionary every five
/// minutes; these tests pin the replacement, and pin it to the same answer
/// AddAsync would give, so the two cannot drift apart.
/// </summary>
public sealed class CatalogueExistenceLookupTests
{
    [Fact]
    public async Task Movie_lookup_matches_on_imdb_id_provider_id_and_title_year()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-19T08:00:00Z"));
        await InitializeMoviesAsync(storage, timeProvider);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);

        var added = await movies.AddAsync(
            new CreateMovieRequest(
                Title: "Arrival",
                ReleaseYear: 2016,
                ImdbId: "tt2543164",
                Monitored: true,
                MetadataProvider: "tmdb",
                MetadataProviderId: "329865",
                OriginalTitle: null,
                Overview: null,
                PosterUrl: null,
                BackdropUrl: null,
                Rating: null,
                Genres: null,
                ExternalUrl: null,
                MetadataJson: null),
            CancellationToken.None);

        // By IMDb id alone, with a title that would not match.
        Assert.Equal(added.Id, await movies.FindExistingIdAsync(
            "Something Else", 1999, "tt2543164", null, null, CancellationToken.None));

        // By provider id alone.
        Assert.Equal(added.Id, await movies.FindExistingIdAsync(
            "Something Else", 1999, null, "tmdb", "329865", CancellationToken.None));

        // By title and year alone, case-insensitively.
        Assert.Equal(added.Id, await movies.FindExistingIdAsync(
            "arrival", 2016, null, null, null, CancellationToken.None));

        // A different year is a different film.
        Assert.Null(await movies.FindExistingIdAsync(
            "Arrival", 2017, null, null, null, CancellationToken.None));

        Assert.Null(await movies.FindExistingIdAsync(
            "Nothing Like This", 2026, "tt0000001", "tmdb", "1", CancellationToken.None));
    }

    [Fact]
    public async Task Movie_lookup_agrees_with_what_adding_the_same_title_would_do()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-19T08:00:00Z"));
        await InitializeMoviesAsync(storage, timeProvider);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);

        var request = new CreateMovieRequest(
            Title: "Conclave",
            ReleaseYear: 2024,
            ImdbId: "tt20215234",
            Monitored: true,
            MetadataProvider: "tmdb",
            MetadataProviderId: "974576",
            OriginalTitle: null,
            Overview: null,
            PosterUrl: null,
            BackdropUrl: null,
            Rating: null,
            Genres: null,
            ExternalUrl: null,
            MetadataJson: null);

        Assert.Null(await movies.FindExistingIdAsync(
            request.Title!, request.ReleaseYear, request.ImdbId, request.MetadataProvider, request.MetadataProviderId,
            CancellationToken.None));

        var added = await movies.AddAsync(request, CancellationToken.None);

        Assert.Equal(added.Id, await movies.FindExistingIdAsync(
            request.Title!, request.ReleaseYear, request.ImdbId, request.MetadataProvider, request.MetadataProviderId,
            CancellationToken.None));

        // The point of the agreement: adding again lands on the same row, so a
        // caller that trusts the lookup and skips the add is not wrong.
        var addedAgain = await movies.AddAsync(request, CancellationToken.None);
        Assert.Equal(added.Id, addedAgain.Id);
        Assert.Single(await movies.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Series_lookup_matches_on_imdb_id_provider_id_and_title_year()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-19T08:00:00Z"));
        await InitializeSeriesAsync(storage, timeProvider);
        var series = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);

        var added = await series.AddAsync(
            new CreateSeriesRequest(
                Title: "Breaking Bad",
                StartYear: 2008,
                ImdbId: "tt0903747",
                Monitored: true,
                MetadataProvider: "tmdb",
                MetadataProviderId: "1396",
                OriginalTitle: null,
                Overview: null,
                PosterUrl: null,
                BackdropUrl: null,
                Rating: null,
                Genres: null,
                ExternalUrl: null,
                MetadataJson: null),
            CancellationToken.None);

        Assert.Equal(added.Id, await series.FindExistingIdAsync(
            "Unrelated", 1999, "tt0903747", null, null, CancellationToken.None));
        Assert.Equal(added.Id, await series.FindExistingIdAsync(
            "Unrelated", 1999, null, "tmdb", "1396", CancellationToken.None));
        Assert.Equal(added.Id, await series.FindExistingIdAsync(
            "breaking bad", 2008, null, null, null, CancellationToken.None));
        Assert.Null(await series.FindExistingIdAsync(
            "Nothing Like This", 2026, null, null, null, CancellationToken.None));
    }

    private static async Task InitializeMoviesAsync(TestStorage storage, TimeProvider timeProvider)
    {
        var migrator = new SqliteDatabaseMigrator(storage.Factory, timeProvider);
        await new MoviesSchemaInitializer(
            storage.Factory,
            migrator,
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
    }

    private static async Task InitializeSeriesAsync(TestStorage storage, TimeProvider timeProvider)
    {
        var migrator = new SqliteDatabaseMigrator(storage.Factory, timeProvider);
        await new SeriesSchemaInitializer(
            storage.Factory,
            migrator,
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
    }
}
