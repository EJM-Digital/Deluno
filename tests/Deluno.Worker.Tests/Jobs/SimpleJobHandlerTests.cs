using System.Text.Json;
using Deluno.Integrations.Metadata;
using Deluno.Jobs.Contracts;
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
            .Setup(repository => repository.UpdateMetadataAsync("movie-1", It.IsAny<MetadataSearchResult>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(movie);

        var jobScheduler = new Mock<IJobScheduler>(MockBehavior.Strict);

        var handler = new MoviesCatalogRefreshJobHandler(
            metadataProvider.Object,
            movieCatalogRepository.Object,
            new Mock<IActivityFeedRepository>().Object,
            jobScheduler.Object);

        var job = TestJobs.Create("movies.catalog.refresh", relatedEntityId: "movie-1");
        var message = await handler.HandleAsync(job, CancellationToken.None);

        Assert.Equal("Refreshed metadata for Arrival.", message);
        movieCatalogRepository.Verify(
            repository => repository.UpdateMetadataAsync("movie-1", It.IsAny<MetadataSearchResult>(), It.IsAny<CancellationToken>()),
            Times.Once);
        // No pendingSearches on the payload (a scheduled or manual refresh,
        // not one from the add endpoint) means nothing else is queued.
        jobScheduler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MoviesCatalogRefreshJobHandler_queues_the_adds_own_search_after_the_refresh_lands()
    {
        // #645/#646 follow-up: the add endpoint used to enqueue its targeted
        // search job side by side with this refresh job, so whichever worker
        // picked up first decided whether the search ran with fresh metadata
        // or none at all. The search request now rides along on this job's
        // own payload and is queued here, after the refresh has run.
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
            .Setup(repository => repository.UpdateMetadataAsync("movie-1", It.IsAny<MetadataSearchResult>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(movie);

        EnqueueJobRequest? enqueued = null;
        var jobScheduler = new Mock<IJobScheduler>();
        jobScheduler
            .Setup(scheduler => scheduler.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()))
            .Callback<EnqueueJobRequest, CancellationToken>((request, _) => enqueued = request)
            .ReturnsAsync(TestJobs.Create("library.search.movies"));

        var handler = new MoviesCatalogRefreshJobHandler(
            metadataProvider.Object,
            movieCatalogRepository.Object,
            new Mock<IActivityFeedRepository>().Object,
            jobScheduler.Object);

        var payload = JsonSerializer.Serialize(new
        {
            id = "movie-1",
            title = "Arrival",
            imdbId = "tt2543164",
            pendingSearches = new[]
            {
                new
                {
                    libraryId = "library-movies",
                    libraryName = "Films",
                    mediaType = "movies",
                    retryDelayHours = 6,
                    triggeredBy = "movie-added",
                    targetEntityId = "movie-1"
                }
            }
        });
        var job = TestJobs.Create("movies.catalog.refresh", payloadJson: payload, relatedEntityId: "movie-1");
        await handler.HandleAsync(job, CancellationToken.None);

        Assert.NotNull(enqueued);
        Assert.Equal("library.search.movies", enqueued!.JobType);
        Assert.Equal("movie", enqueued.RelatedEntityType);
        Assert.Equal("movie-1", enqueued.RelatedEntityId);
        Assert.Contains("\"triggeredBy\":\"movie-added\"", enqueued.PayloadJson);
        Assert.Contains("\"targetEntityId\":\"movie-1\"", enqueued.PayloadJson);
        Assert.Contains("\"libraryId\":\"library-movies\"", enqueued.PayloadJson);
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

        var jobScheduler = new Mock<IJobScheduler>(MockBehavior.Strict);

        var handler = new SeriesCatalogRefreshJobHandler(
            metadataProvider.Object,
            seriesCatalogRepository.Object,
            new Mock<IActivityFeedRepository>(MockBehavior.Strict).Object,
            jobScheduler.Object);

        var job = TestJobs.Create("series.catalog.refresh", relatedEntityId: "series-1");
        var message = await handler.HandleAsync(job, CancellationToken.None);

        Assert.Equal("No metadata match found for Severance.", message);
        metadataProvider.Verify(
            provider => provider.SearchAsync(It.IsAny<MetadataLookupRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
        // No pendingSearches on the payload means nothing else is queued.
        jobScheduler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SeriesCatalogRefreshJobHandler_queues_the_adds_own_search_after_the_refresh_lands()
    {
        // #645/#646 follow-up (the defect proven live on the rig): adding a
        // monitored show used to enqueue series.catalog.refresh (which learns
        // the episode list) and the targeted library.search.tv job side by
        // side. The search often ran first — a freshly-added show has no
        // episode rows yet — found nothing to plan, and was never searched
        // again until the library's own next scheduled cycle. The search
        // request now rides along on the refresh job's own payload and is
        // queued from here, after the refresh (which learns the episodes) has
        // actually run — even when the metadata search itself found nothing,
        // since a show search does not depend on having episodes on record.
        var series = new SeriesListItem(
            "series-1", "Severance", 2022, "tt11280740", true, true,
            null, null, null, null, null, null, null, [], null, null, null, null, Now, Now);

        var metadataProvider = new Mock<IMetadataProvider>();
        metadataProvider
            .Setup(provider => provider.SearchAsync(It.IsAny<MetadataLookupRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var seriesCatalogRepository = new Mock<ISeriesCatalogRepository>();
        seriesCatalogRepository.Setup(repository => repository.GetByIdAsync("series-1", It.IsAny<CancellationToken>())).ReturnsAsync(series);

        EnqueueJobRequest? enqueued = null;
        var jobScheduler = new Mock<IJobScheduler>();
        jobScheduler
            .Setup(scheduler => scheduler.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()))
            .Callback<EnqueueJobRequest, CancellationToken>((request, _) => enqueued = request)
            .ReturnsAsync(TestJobs.Create("library.search.tv"));

        var handler = new SeriesCatalogRefreshJobHandler(
            metadataProvider.Object,
            seriesCatalogRepository.Object,
            new Mock<IActivityFeedRepository>(MockBehavior.Strict).Object,
            jobScheduler.Object);

        var payload = JsonSerializer.Serialize(new
        {
            id = "series-1",
            title = "Severance",
            imdbId = "tt11280740",
            pendingSearches = new[]
            {
                new
                {
                    libraryId = "library-tv",
                    libraryName = "TV Shows",
                    mediaType = "tv",
                    retryDelayHours = 6,
                    triggeredBy = "series-added",
                    targetEntityId = "series-1"
                }
            }
        });
        var job = TestJobs.Create("series.catalog.refresh", payloadJson: payload, relatedEntityId: "series-1");
        var message = await handler.HandleAsync(job, CancellationToken.None);

        Assert.Equal("No metadata match found for Severance.", message);
        Assert.NotNull(enqueued);
        Assert.Equal("library.search.tv", enqueued!.JobType);
        Assert.Equal("series", enqueued.RelatedEntityType);
        Assert.Equal("series-1", enqueued.RelatedEntityId);
        Assert.Contains("\"triggeredBy\":\"series-added\"", enqueued.PayloadJson);
        Assert.Contains("\"targetEntityId\":\"series-1\"", enqueued.PayloadJson);
        Assert.Contains("\"libraryId\":\"library-tv\"", enqueued.PayloadJson);
    }
}
