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

public sealed class MetadataRefreshJobHandlerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-04-29T03:00:00Z");

    [Fact]
    public async Task MoviesMetadataRefreshJobHandler_no_related_movie_skips_without_calling_the_provider()
    {
        var metadataProvider = new Mock<IMetadataProvider>(MockBehavior.Strict);
        var movieCatalogRepository = new Mock<IMovieCatalogRepository>(MockBehavior.Strict);
        var activityFeedRepository = new Mock<IActivityFeedRepository>(MockBehavior.Strict);
        var handler = new MoviesMetadataRefreshJobHandler(metadataProvider.Object, movieCatalogRepository.Object, activityFeedRepository.Object);

        var message = await handler.HandleAsync(TestJobs.Create("movies.metadata.refresh"), CancellationToken.None);

        Assert.Equal("Movie metadata refresh skipped because no movie was linked.", message);
    }

    [Fact]
    public async Task MoviesMetadataRefreshJobHandler_movie_no_longer_exists_skips()
    {
        var metadataProvider = new Mock<IMetadataProvider>(MockBehavior.Strict);
        var movieCatalogRepository = new Mock<IMovieCatalogRepository>();
        movieCatalogRepository.Setup(repository => repository.GetByIdAsync("movie-1", It.IsAny<CancellationToken>())).ReturnsAsync((MovieListItem?)null);
        var activityFeedRepository = new Mock<IActivityFeedRepository>(MockBehavior.Strict);
        var handler = new MoviesMetadataRefreshJobHandler(metadataProvider.Object, movieCatalogRepository.Object, activityFeedRepository.Object);

        var job = TestJobs.Create("movies.metadata.refresh", relatedEntityId: "movie-1");
        var message = await handler.HandleAsync(job, CancellationToken.None);

        Assert.Equal("Movie metadata refresh skipped because the movie no longer exists.", message);
    }

    [Fact]
    public async Task MoviesMetadataRefreshJobHandler_match_found_updates_metadata_and_records_activity()
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

        var activityFeedRepository = new Mock<IActivityFeedRepository>();
        var handler = new MoviesMetadataRefreshJobHandler(metadataProvider.Object, movieCatalogRepository.Object, activityFeedRepository.Object);

        var job = TestJobs.Create("movies.metadata.refresh", relatedEntityId: "movie-1");
        var message = await handler.HandleAsync(job, CancellationToken.None);

        Assert.Equal("Refreshed metadata for Arrival.", message);
        movieCatalogRepository.Verify(
            repository => repository.UpdateMetadataAsync("movie-1", It.IsAny<MetadataSearchResult>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task MoviesMetadataRefreshJobHandler_missing_linked_record_keeps_movie_and_records_one_title_issue()
    {
        var movie = new MovieListItem(
            "movie-1", "State of Siege: Temple Attack", 2021, null, true, true,
            "tmdb", "1603343", null, "stored overview", null, null, null, [], null, null, null, Now, Now, Now);
        var lookup = new MetadataProviderRecordLookup(
            MetadataProviderRecordStatus.Missing,
            "tmdb",
            "1603343",
            Failure: Deluno.Contracts.IntegrationFailureFactory.FromLegacy(
                "metadata", "tmdb", "tmdb", "metadata.provider.resolve", "notfound", "HTTP 404", 404));

        var metadataProvider = new Mock<IMetadataProvider>(MockBehavior.Strict);
        metadataProvider
            .Setup(provider => provider.ResolveProviderRecordAsync(It.IsAny<MetadataLookupRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(lookup);

        var movieCatalogRepository = new Mock<IMovieCatalogRepository>();
        movieCatalogRepository.Setup(repository => repository.GetByIdAsync(movie.Id, It.IsAny<CancellationToken>())).ReturnsAsync(movie);
        movieCatalogRepository.Setup(repository => repository.RecordMetadataProviderIssueAsync(
            movie.Id,
            It.IsAny<MetadataProviderIssue>(),
            It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var activityFeedRepository = new Mock<IActivityFeedRepository>();
        var handler = new MoviesMetadataRefreshJobHandler(metadataProvider.Object, movieCatalogRepository.Object, activityFeedRepository.Object);

        var message = await handler.HandleAsync(
            TestJobs.Create("movies.metadata.refresh", relatedEntityId: movie.Id),
            CancellationToken.None);

        Assert.Equal("Kept State of Siege: Temple Attack; its linked TMDB record is no longer available.", message);
        movieCatalogRepository.Verify(repository => repository.RecordMetadataProviderIssueAsync(
            movie.Id,
            It.Is<MetadataProviderIssue>(issue => issue.ProviderId == "1603343" && issue.AcknowledgedUtc == null),
            It.IsAny<CancellationToken>()), Times.Once);
        movieCatalogRepository.Verify(repository => repository.UpdateMetadataAsync(
            It.IsAny<string>(), It.IsAny<MetadataSearchResult>(), It.IsAny<CancellationToken>()), Times.Never);
        metadataProvider.Verify(provider => provider.SearchAsync(
            It.IsAny<MetadataLookupRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SeriesMetadataRefreshJobHandler_no_related_series_skips_without_calling_the_provider()
    {
        var metadataProvider = new Mock<IMetadataProvider>(MockBehavior.Strict);
        var seriesCatalogRepository = new Mock<ISeriesCatalogRepository>(MockBehavior.Strict);
        var activityFeedRepository = new Mock<IActivityFeedRepository>(MockBehavior.Strict);
        var handler = new SeriesMetadataRefreshJobHandler(metadataProvider.Object, seriesCatalogRepository.Object, activityFeedRepository.Object);

        var message = await handler.HandleAsync(TestJobs.Create("series.metadata.refresh"), CancellationToken.None);

        Assert.Equal("TV metadata refresh skipped because no series was linked.", message);
    }

    [Fact]
    public async Task SeriesMetadataRefreshJobHandler_no_match_reports_that_nothing_was_found()
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

        var activityFeedRepository = new Mock<IActivityFeedRepository>(MockBehavior.Strict);
        var handler = new SeriesMetadataRefreshJobHandler(metadataProvider.Object, seriesCatalogRepository.Object, activityFeedRepository.Object);

        var job = TestJobs.Create("series.metadata.refresh", relatedEntityId: "series-1");
        var message = await handler.HandleAsync(job, CancellationToken.None);

        Assert.Equal("No metadata match found for Severance.", message);
    }

    [Fact]
    public async Task SeriesMetadataRefreshJobHandler_repeated_missing_linked_record_keeps_populated_show_and_records_one_activity()
    {
        var series = new SeriesListItem(
            "series-1", "A Removed Show", 2020, "tt1234567", true, true,
            "tmdb", "999999999", null, "stored overview", null, null, null, [], null, null, null, Now, Now, Now);
        var lookup = new MetadataProviderRecordLookup(
            MetadataProviderRecordStatus.Missing,
            "tmdb",
            "999999999",
            Failure: Deluno.Contracts.IntegrationFailureFactory.FromLegacy(
                "metadata", "tmdb", "tmdb", "metadata.provider.resolve", "notfound", "HTTP 404", 404));

        var metadataProvider = new Mock<IMetadataProvider>(MockBehavior.Strict);
        metadataProvider
            .Setup(provider => provider.ResolveProviderRecordAsync(It.IsAny<MetadataLookupRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(lookup);

        MetadataProviderIssue? storedIssue = null;
        var seriesCatalogRepository = new Mock<ISeriesCatalogRepository>();
        seriesCatalogRepository.Setup(repository => repository.GetByIdAsync(series.Id, It.IsAny<CancellationToken>())).ReturnsAsync(series);
        seriesCatalogRepository
            .Setup(repository => repository.GetMetadataProviderIssueAsync(series.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => storedIssue);
        seriesCatalogRepository
            .Setup(repository => repository.RecordMetadataProviderIssueAsync(
                series.Id,
                It.IsAny<MetadataProviderIssue>(),
                It.IsAny<CancellationToken>()))
            .Returns((string _, MetadataProviderIssue issue, CancellationToken _) =>
            {
                var isNewEvidence = !string.Equals(storedIssue?.EvidenceKey, issue.EvidenceKey, StringComparison.Ordinal);
                storedIssue ??= issue;
                return Task.FromResult(isNewEvidence);
            });

        var activityFeedRepository = new Mock<IActivityFeedRepository>();
        var handler = new SeriesMetadataRefreshJobHandler(metadataProvider.Object, seriesCatalogRepository.Object, activityFeedRepository.Object);
        var job = TestJobs.Create("series.metadata.refresh", relatedEntityId: series.Id);

        var firstMessage = await handler.HandleAsync(job, CancellationToken.None);
        var secondMessage = await handler.HandleAsync(job, CancellationToken.None);

        Assert.Equal("Kept A Removed Show; its linked TMDB record is no longer available.", firstMessage);
        Assert.Equal(firstMessage, secondMessage);
        Assert.NotNull(storedIssue);
        Assert.Equal("tmdb:series:999999999:missing", storedIssue.EvidenceKey);
        seriesCatalogRepository.Verify(repository => repository.RecordMetadataProviderIssueAsync(
            series.Id,
            It.Is<MetadataProviderIssue>(issue => issue.ProviderId == "999999999" && issue.AcknowledgedUtc == null),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
        seriesCatalogRepository.Verify(repository => repository.UpdateMetadataAsync(
            It.IsAny<string>(), It.IsAny<MetadataSearchResult>(), It.IsAny<CancellationToken>()), Times.Never);
        metadataProvider.Verify(provider => provider.SearchAsync(
            It.IsAny<MetadataLookupRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        activityFeedRepository.Verify(repository => repository.RecordActivityAsync(
            "metadata.series.provider-record-missing",
            It.Is<string>(message => message.Contains("was kept", StringComparison.Ordinal)),
            It.IsAny<string?>(),
            job.Id,
            "series",
            series.Id,
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
