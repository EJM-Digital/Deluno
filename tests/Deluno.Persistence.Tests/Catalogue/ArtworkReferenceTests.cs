using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Movies.Migrations;
using Deluno.Series.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Catalogue;

public sealed class ArtworkReferenceTests
{
    [Fact]
    public async Task Movie_and_series_catalogues_return_only_valid_local_artwork_keys()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-05-14T00:00:00Z"));

        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var movieKey = new string('a', 64);
        var seriesKey = new string('b', 64);
        var movieRepository = new SqliteMovieCatalogRepository(storage.Factory, clock);
        var seriesRepository = new SqliteSeriesCatalogRepository(storage.Factory, clock);

        await movieRepository.AddAsync(
            new CreateMovieRequest(
                "Referenced movie",
                2026,
                null,
                PosterUrl: LocalArtworkUrl(movieKey),
                BackdropUrl: "/api/metadata/artwork/not-a-cache-key"),
            CancellationToken.None);
        await seriesRepository.AddAsync(
            new CreateSeriesRequest(
                "Referenced show",
                2026,
                null,
                PosterUrl: LocalArtworkUrl(seriesKey)),
            CancellationToken.None);

        var movieKeys = await movieRepository.ListReferencedArtworkCacheKeysAsync(CancellationToken.None);
        var seriesKeys = await seriesRepository.ListReferencedArtworkCacheKeysAsync(CancellationToken.None);

        Assert.Equal([movieKey], movieKeys.OrderBy(key => key));
        Assert.Equal([seriesKey], seriesKeys.OrderBy(key => key));
    }

    private static string LocalArtworkUrl(string key) => $"/api/metadata/artwork/{key}";
}
