using Deluno.Integrations.Metadata;
using Deluno.Jobs.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

// The catalog-refresh handlers used to be stubs that reported success without
// doing anything, which left a newly added title with no metadata or episodes
// until someone pressed Refresh (#245). These tests pin the repaired
// behaviour: the add-time job runs the same per-title refresh the scheduled
// metadata job uses.
public sealed class SimpleJobHandlerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-04-29T03:00:00Z");

    [Fact]
    public async Task MoviesCatalogRefreshJobHandler_runs_the_real_metadata_refresh()
    {
        var movie = new MovieListItem(
            "movie-1", "Arrival", 2016, "tt2543164", true, true,
            null, null, null, null, null, null, null, [], null, null, null, null, Now, Now);

        var metadataProvider = new Mock<IMetadataProvider>();
        metadataProvider
            .Setup(provider => provider.SearchAsync(It.IsAny<MetadataLookupRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MetadataSearchResult("tmdb", "329865", "movies", "Arrival", null, 2016, "overview", null, null, 7.6, [], ["Drama"], "tt2543164", null)]);

        var movieCatalogRepository = new Mock<IMovieCatalogRepository>();
        movieCatalogRepository.Setup(repository => repository.GetByIdAsync("movie-1", It.IsAny<CancellationToken>())).ReturnsAsync(movie);
        movieCatalogRepository
            .Setup(repository => repository.UpdateMetadataAsync(
                "movie-1", "tmdb", "329865", null, "overview", null, null, 7.6, "Drama", null, "tt2543164", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(movie);

        var handler = new MoviesCatalogRefreshJobHandler(
            metadataProvider.Object,
            movieCatalogRepository.Object,
            new Mock<IActivityFeedRepository>().Object);

        var job = TestJobs.Create("movies.catalog.refresh", relatedEntityId: "movie-1");
        var message = await handler.HandleAsync(job, CancellationToken.None);

        Assert.Equal("Refreshed metadata for Arrival.", message);
        movieCatalogRepository.Verify(
            repository => repository.UpdateMetadataAsync(
                "movie-1", "tmdb", "329865", null, "overview", null, null, 7.6, "Drama", null, "tt2543164", It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SeriesCatalogRefreshJobHandler_runs_the_real_metadata_refresh()
    {
        var series = new SeriesListItem(
            "series-1", "Severance", 2022, "tt11280740", true, true,
            null, null, null, null, null, null, null, [], null, null, null, null, Now, Now);

        var metadataProvider = new Mock<IMetadataProvider>();
        metadataProvider
            .Setup(provider => provider.SearchAsync(It.IsAny<MetadataLookupRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var seriesCatalogRepository = new Mock<ISeriesCatalogRepository>();
        seriesCatalogRepository.Setup(repository => repository.GetByIdAsync("series-1", It.IsAny<CancellationToken>())).ReturnsAsync(series);

        var handler = new SeriesCatalogRefreshJobHandler(
            metadataProvider.Object,
            seriesCatalogRepository.Object,
            new Mock<IActivityFeedRepository>(MockBehavior.Strict).Object);

        var job = TestJobs.Create("series.catalog.refresh", relatedEntityId: "series-1");
        var message = await handler.HandleAsync(job, CancellationToken.None);

        Assert.Equal("No metadata match found for Severance.", message);
        metadataProvider.Verify(
            provider => provider.SearchAsync(It.IsAny<MetadataLookupRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
