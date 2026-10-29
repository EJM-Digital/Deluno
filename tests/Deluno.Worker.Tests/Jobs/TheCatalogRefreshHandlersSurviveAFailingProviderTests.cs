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

/// <summary>
/// The add's own search rides on the refresh job's payload. A provider that is down, a refresh that throws or a
/// payload that cannot be read must neither lose that search nor queue it wrongly.
/// </summary>
public sealed class TheCatalogRefreshHandlersSurviveAFailingProviderTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-04-29T03:00:00Z");

    private static string PayloadWithASearch(string mediaType, string target) => JsonSerializer.Serialize(new
    {
        pendingSearches = new[]
        {
            new { libraryId = "library-1", libraryName = "Library", mediaType, retryDelayHours = 6, triggeredBy = "added", targetEntityId = target }
        }
    });

    private static Mock<IJobScheduler> Scheduler(List<EnqueueJobRequest> enqueued)
    {
        var scheduler = new Mock<IJobScheduler>();
        scheduler
            .Setup(s => s.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()))
            .Callback<EnqueueJobRequest, CancellationToken>((request, _) => enqueued.Add(request))
            .ReturnsAsync(TestJobs.Create("library.search"));
        return scheduler;
    }

    private static (Mock<IMetadataProvider> Provider, Mock<IMovieCatalogRepository> Repository) MovieSetup()
    {
        var movie = new MovieListItem(
            "movie-1", "Arrival", 2016, "tt2543164", true, true,
            null, null, null, null, null, null, null, [], null, null, null, null, Now, Now);
        var repository = new Mock<IMovieCatalogRepository>();
        repository.Setup(r => r.GetByIdAsync("movie-1", It.IsAny<CancellationToken>())).ReturnsAsync(movie);
        return (new Mock<IMetadataProvider>(), repository);
    }

    private static (Mock<IMetadataProvider> Provider, Mock<ISeriesCatalogRepository> Repository) SeriesSetup()
    {
        var series = new SeriesListItem(
            "series-1", "Severance", 2022, "tt11280740", true, true,
            null, null, null, null, null, null, null, [], null, null, null, null, Now, Now);
        var repository = new Mock<ISeriesCatalogRepository>();
        repository.Setup(r => r.GetByIdAsync("series-1", It.IsAny<CancellationToken>())).ReturnsAsync(series);
        return (new Mock<IMetadataProvider>(), repository);
    }

    [Fact]
    public async Task A_film_whose_provider_throws_fails_the_job_and_queues_no_search_so_the_retry_carries_it()
    {
        var (provider, repository) = MovieSetup();
        provider.Setup(p => p.SearchAsync(It.IsAny<MetadataLookupRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("provider down"));
        var enqueued = new List<EnqueueJobRequest>();
        var handler = new MoviesCatalogRefreshJobHandler(provider.Object, repository.Object, new Mock<IActivityFeedRepository>().Object, Scheduler(enqueued).Object);

        await Assert.ThrowsAsync<HttpRequestException>(() => handler.HandleAsync(
            TestJobs.Create("movies.catalog.refresh", payloadJson: PayloadWithASearch("movies", "movie-1"), relatedEntityId: "movie-1"),
            CancellationToken.None));

        Assert.Empty(enqueued);
    }

    [Fact]
    public async Task A_show_whose_provider_throws_fails_the_job_and_queues_no_search_so_the_retry_carries_it()
    {
        var (provider, repository) = SeriesSetup();
        provider.Setup(p => p.SearchAsync(It.IsAny<MetadataLookupRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("provider down"));
        var enqueued = new List<EnqueueJobRequest>();
        var handler = new SeriesCatalogRefreshJobHandler(provider.Object, repository.Object, new Mock<IActivityFeedRepository>().Object, Scheduler(enqueued).Object);

        await Assert.ThrowsAsync<HttpRequestException>(() => handler.HandleAsync(
            TestJobs.Create("series.catalog.refresh", payloadJson: PayloadWithASearch("tv", "series-1"), relatedEntityId: "series-1"),
            CancellationToken.None));

        Assert.Empty(enqueued);
    }

    [Fact]
    public async Task A_film_whose_provider_finds_nothing_still_queues_the_adds_own_search_once()
    {
        var (provider, repository) = MovieSetup();
        provider.Setup(p => p.SearchAsync(It.IsAny<MetadataLookupRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var enqueued = new List<EnqueueJobRequest>();
        var handler = new MoviesCatalogRefreshJobHandler(provider.Object, repository.Object, new Mock<IActivityFeedRepository>().Object, Scheduler(enqueued).Object);

        var message = await handler.HandleAsync(
            TestJobs.Create("movies.catalog.refresh", payloadJson: PayloadWithASearch("movies", "movie-1"), relatedEntityId: "movie-1"),
            CancellationToken.None);

        Assert.Equal("No metadata match found for Arrival.", message);
        var search = Assert.Single(enqueued);
        Assert.Equal("movie", search.RelatedEntityType);
        Assert.Equal("movie-1", search.RelatedEntityId);
    }

    [Fact]
    public async Task A_show_whose_provider_finds_nothing_still_queues_the_adds_own_search_once()
    {
        var (provider, repository) = SeriesSetup();
        provider.Setup(p => p.SearchAsync(It.IsAny<MetadataLookupRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var enqueued = new List<EnqueueJobRequest>();
        var handler = new SeriesCatalogRefreshJobHandler(provider.Object, repository.Object, new Mock<IActivityFeedRepository>().Object, Scheduler(enqueued).Object);

        var message = await handler.HandleAsync(
            TestJobs.Create("series.catalog.refresh", payloadJson: PayloadWithASearch("tv", "series-1"), relatedEntityId: "series-1"),
            CancellationToken.None);

        Assert.Equal("No metadata match found for Severance.", message);
        var search = Assert.Single(enqueued);
        Assert.Equal("series", search.RelatedEntityType);
        Assert.Equal("series-1", search.RelatedEntityId);
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("[]")]
    [InlineData("{\"pendingSearches\":[]}")]
    public async Task A_payload_that_cannot_be_read_or_names_no_search_queues_nothing_and_the_refresh_still_runs(string payload)
    {
        var (provider, repository) = MovieSetup();
        provider.Setup(p => p.SearchAsync(It.IsAny<MetadataLookupRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var enqueued = new List<EnqueueJobRequest>();
        var handler = new MoviesCatalogRefreshJobHandler(provider.Object, repository.Object, new Mock<IActivityFeedRepository>().Object, Scheduler(enqueued).Object);

        var message = await handler.HandleAsync(
            TestJobs.Create("movies.catalog.refresh", payloadJson: payload, relatedEntityId: "movie-1"),
            CancellationToken.None);

        Assert.Equal("No metadata match found for Arrival.", message);
        Assert.Empty(enqueued);
    }

    [Fact]
    public async Task A_cancelled_refresh_stops_before_any_search_is_queued()
    {
        var (provider, repository) = MovieSetup();
        provider.Setup(p => p.SearchAsync(It.IsAny<MetadataLookupRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var enqueued = new List<EnqueueJobRequest>();
        var handler = new MoviesCatalogRefreshJobHandler(provider.Object, repository.Object, new Mock<IActivityFeedRepository>().Object, Scheduler(enqueued).Object);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.HandleAsync(
            TestJobs.Create("movies.catalog.refresh", payloadJson: PayloadWithASearch("movies", "movie-1"), relatedEntityId: "movie-1"),
            new CancellationToken(true)));

        Assert.Empty(enqueued);
    }
}
