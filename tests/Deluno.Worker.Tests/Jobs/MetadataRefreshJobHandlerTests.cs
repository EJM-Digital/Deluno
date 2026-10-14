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
        metadataProvider.Setup(provider => provider.BeginFailureScope()).Returns(NoMetadataFailureScope.Instance);
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
    public async Task MoviesMetadataRefreshJobHandler_never_moves_a_tmdb_linked_title_onto_omdb()
    {
        var movie = new MovieListItem(
            "movie-1", "Arrival", 2016, "tt2543164", true, true,
            "tmdb", null, null, null, null, null, null, [], null, null, null, null, Now, Now);
        var metadataProvider = new Mock<IMetadataProvider>();
        metadataProvider
            .Setup(provider => provider.SearchAsync(It.IsAny<MetadataLookupRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MetadataSearchResult("omdb", "tt2543164", "movies", "Arrival", null, 2016, "overview", null, null, 7.6, [], [], "tt2543164", null)]);
        var movieCatalogRepository = new Mock<IMovieCatalogRepository>();
        movieCatalogRepository.Setup(repository => repository.GetByIdAsync("movie-1", It.IsAny<CancellationToken>())).ReturnsAsync(movie);
        var handler = new MoviesMetadataRefreshJobHandler(metadataProvider.Object, movieCatalogRepository.Object, new Mock<IActivityFeedRepository>().Object);

        var message = await handler.HandleAsync(TestJobs.Create("movies.metadata.refresh", relatedEntityId: "movie-1"), CancellationToken.None);

        Assert.Contains("Fix match", message, StringComparison.Ordinal);
        movieCatalogRepository.Verify(repository => repository.UpdateMetadataAsync(
            It.IsAny<string>(), It.IsAny<MetadataSearchResult>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SeriesMetadataRefreshJobHandler_never_moves_a_tvdb_linked_show_onto_omdb()
    {
        var series = new SeriesListItem(
            "series-1", "Severance", 2022, "tt11280740", true, true,
            "tvdb", null, null, null, null, null, null, [], null, null, null, null, Now, Now);
        var metadataProvider = new Mock<IMetadataProvider>();
        metadataProvider
            .Setup(provider => provider.SearchAsync(It.IsAny<MetadataLookupRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MetadataSearchResult("omdb", "tt11280740", "tv", "Severance", null, 2022, "overview", null, null, 8.7, [], [], "tt11280740", null)]);
        var seriesCatalogRepository = new Mock<ISeriesCatalogRepository>();
        seriesCatalogRepository.Setup(repository => repository.GetByIdAsync("series-1", It.IsAny<CancellationToken>())).ReturnsAsync(series);
        var handler = new SeriesMetadataRefreshJobHandler(metadataProvider.Object, seriesCatalogRepository.Object, new Mock<IActivityFeedRepository>().Object);

        var message = await handler.HandleAsync(TestJobs.Create("series.metadata.refresh", relatedEntityId: "series-1"), CancellationToken.None);

        Assert.Contains("Fix match", message, StringComparison.Ordinal);
        seriesCatalogRepository.Verify(repository => repository.UpdateMetadataAsync(
            It.IsAny<string>(), It.IsAny<MetadataSearchResult>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MoviesMetadataRefreshJobHandler_an_unlinked_title_may_still_be_matched_on_omdb()
    {
        var movie = new MovieListItem(
            "movie-1", "Arrival", 2016, "tt2543164", true, true,
            null, null, null, null, null, null, null, [], null, null, null, null, Now, Now);
        var metadataProvider = new Mock<IMetadataProvider>();
        metadataProvider
            .Setup(provider => provider.SearchAsync(It.IsAny<MetadataLookupRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MetadataSearchResult("omdb", "tt2543164", "movies", "Arrival", null, 2016, "overview", null, null, 7.6, [], [], "tt2543164", null)]);
        var movieCatalogRepository = new Mock<IMovieCatalogRepository>();
        movieCatalogRepository.Setup(repository => repository.GetByIdAsync("movie-1", It.IsAny<CancellationToken>())).ReturnsAsync(movie);
        movieCatalogRepository
            .Setup(repository => repository.UpdateMetadataAsync("movie-1", It.IsAny<MetadataSearchResult>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(movie);
        var handler = new MoviesMetadataRefreshJobHandler(metadataProvider.Object, movieCatalogRepository.Object, new Mock<IActivityFeedRepository>().Object);

        var message = await handler.HandleAsync(TestJobs.Create("movies.metadata.refresh", relatedEntityId: "movie-1"), CancellationToken.None);

        Assert.Equal("Refreshed metadata for Arrival.", message);
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
        metadataProvider.Setup(provider => provider.BeginFailureScope()).Returns(NoMetadataFailureScope.Instance);
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

    private static (SeriesMetadataRefreshJobHandler Handler, Mock<ISeriesCatalogRepository> Series, Mock<IActivityFeedRepository> Activity, JobQueueItem Job)
        BuildTvdbNumberedShow(IReadOnlyList<MetadataSeason> preferred, IReadOnlyList<MetadataSeason> tmdbOnly, Deluno.Contracts.IHealthCheckRepository? health = null)
    {
        var series = new SeriesListItem(
            "series-1", "A Show", 2020, "tt1234567", true, true,
            "tmdb", "42", null, null, null, null, null, [], null, null, null, Now, Now, Now);
        var match = new MetadataSearchResult(
            "tmdb", "42", "tv", "A Show", null, 2020, null, null, null, null, [], [], null, null);
        var metadataProvider = new Mock<IMetadataProvider>();
        metadataProvider
            .Setup(provider => provider.ResolveProviderRecordAsync(It.IsAny<MetadataLookupRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetadataProviderRecordLookup(MetadataProviderRecordStatus.Found, "tmdb", "42", match));
        metadataProvider
            .Setup(provider => provider.GetSeriesCatalogueAsync("42", MetadataCatalogueSource.Preferred, It.IsAny<CancellationToken>()))
            .ReturnsAsync(preferred);
        metadataProvider
            .Setup(provider => provider.GetSeriesCatalogueAsync("42", MetadataCatalogueSource.TmdbOnly, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tmdbOnly);

        SeriesEpisodeInventoryItem Episode(int number, string title, DateTimeOffset air) => new(
            $"e{number}", 1, number, title, null, air, true, true, "have", "", true, null, null, false, null, null, null, Now,
            CatalogueSource: "tvdb");
        var inventory = new SeriesInventoryDetail(
            "series-1", "A Show", 2020, 1, 4, 4,
            [
                Episode(1, "Pilot", Now),
                Episode(2, "Second", Now.AddDays(7)),
                Episode(3, "Third", Now.AddDays(14)),
                Episode(4, "Fourth", Now.AddDays(21))
            ]);
        var seriesRepository = new Mock<ISeriesCatalogRepository>();
        seriesRepository.Setup(repository => repository.GetByIdAsync("series-1", It.IsAny<CancellationToken>())).ReturnsAsync(series);
        seriesRepository.Setup(repository => repository.GetInventoryDetailAsync("series-1", It.IsAny<CancellationToken>())).ReturnsAsync(inventory);
        var activity = new Mock<IActivityFeedRepository>();
        var handler = new SeriesMetadataRefreshJobHandler(metadataProvider.Object, seriesRepository.Object, activity.Object, null, health);
        return (handler, seriesRepository, activity, TestJobs.Create("series.metadata.refresh", relatedEntityId: "series-1"));
    }

    private static MetadataSeason TmdbSeason() => new(1, null, 4, null,
    [
        new(1, 1, "Pilot", null, Now, null, null),
        new(1, 2, "Second", null, Now.AddDays(7), null, null),
        new(1, 3, "Third", null, Now.AddDays(14), null, null),
        new(1, 4, "Fourth", null, Now.AddDays(21), null, null)
    ]);

    [Fact]
    public async Task SeriesMetadataRefreshJobHandler_holds_a_TheTVDB_numbered_show_during_a_TheTVDB_outage()
    {
        // TheTVDB is down, so the preferred list is TMDb's.
        var (handler, series, activity, job) = BuildTvdbNumberedShow([TmdbSeason()], [TmdbSeason()]);

        await handler.HandleAsync(job, CancellationToken.None);

        series.Verify(repository => repository.SyncEpisodeCatalogueAsync(
            It.IsAny<string>(), It.IsAny<IReadOnlyList<CatalogueEpisodeItem>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        activity.Verify(repository => repository.RecordActivityAsync(
            "metadata.series.catalogue.held",
            It.Is<string>(message => message.Contains("did not answer", StringComparison.Ordinal)),
            It.IsAny<string?>(), job.Id, "series", "series-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    private static (Mock<Deluno.Contracts.IHealthCheckRepository> Health, Dictionary<string, Deluno.Contracts.HealthCheckResultItem> Rows) InMemoryHealth()
    {
        var rows = new Dictionary<string, Deluno.Contracts.HealthCheckResultItem>();
        var health = new Mock<Deluno.Contracts.IHealthCheckRepository>();
        health.Setup(repository => repository.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => rows.GetValueOrDefault(id));
        health.Setup(repository => repository.UpsertResultsAsync(It.IsAny<IReadOnlyList<Deluno.Contracts.HealthCheckOutcome>>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyList<Deluno.Contracts.HealthCheckOutcome> outcomes, CancellationToken _) =>
            {
                foreach (var outcome in outcomes)
                {
                    rows[outcome.Id] = new Deluno.Contracts.HealthCheckResultItem(
                        outcome.Id, outcome.Category, outcome.Name, outcome.Status, outcome.Message, outcome.Cause, outcome.Fix,
                        outcome.DetailsJson, Now, Now, null);
                }
            })
            .ReturnsAsync([]);
        health.Setup(repository => repository.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string id, CancellationToken _) => rows.Remove(id))
            .Returns(Task.CompletedTask);
        return (health, rows);
    }

    [Fact]
    public async Task A_held_refresh_writes_Activity_once_and_leaves_one_clearable_Health_note_until_it_goes_through()
    {
        var (health, rows) = InMemoryHealth();
        var (handler, _, activity, job) = BuildTvdbNumberedShow([TmdbSeason()], [TmdbSeason()], health.Object);

        await handler.HandleAsync(job, CancellationToken.None);
        await handler.HandleAsync(job, CancellationToken.None);
        await handler.HandleAsync(job, CancellationToken.None);

        activity.Verify(repository => repository.RecordActivityAsync(
            "metadata.series.catalogue.held", It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        health.Verify(repository => repository.UpsertResultsAsync(
            It.Is<IReadOnlyList<Deluno.Contracts.HealthCheckOutcome>>(items => items.Count == 1
                && items[0].Id == Deluno.Series.Services.SeriesCatalogueChoice.OutageHealthId && items[0].Category == Deluno.Contracts.HealthCheckCategories.Notice),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Single(rows);
    }

    [Fact]
    public async Task Two_shows_held_by_a_TheTVDB_outage_at_the_same_moment_both_reach_the_one_note_and_the_Activity_line_is_said_once()
    {
        var (health, rows) = InMemoryHealth();
        // A slow read, so an unguarded read-modify-write would have both start from the same empty list.
        health.Setup(repository => repository.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string id, CancellationToken _) =>
            {
                await Task.Delay(30); // reason: a slow read, so an unguarded read-modify-write would have both start from the same empty list
                lock (rows)
                {
                    return rows.GetValueOrDefault(id);
                }
            });
        var activity = new Mock<IActivityFeedRepository>();

        await Task.WhenAll(Enumerable.Range(1, 8).Select(index => Deluno.Series.Services.SeriesCatalogueChoice.RecordHoldAsync(
            activity.Object, health.Object, MetadataCatalogueHold.TvdbUnavailable, $"c{index}", $"Concurrent {index}", null, CancellationToken.None)));

        var note = Assert.Single(rows.Values);
        Assert.Contains("8 shows", note.Message, StringComparison.Ordinal);
        activity.Verify(repository => repository.RecordActivityAsync(
            "metadata.series.catalogue.held", It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_TheTVDB_outage_over_many_shows_is_one_Health_note_and_one_Activity_line_that_go_when_they_recover()
    {
        var (health, rows) = InMemoryHealth();
        var activity = new Mock<IActivityFeedRepository>();

        foreach (var index in Enumerable.Range(1, 30))
        {
            await Deluno.Series.Services.SeriesCatalogueChoice.RecordHoldAsync(
                activity.Object, health.Object, MetadataCatalogueHold.TvdbUnavailable, $"s{index}", $"Show {index}", null, CancellationToken.None);
            // A second refresh of the same show adds nothing.
            await Deluno.Series.Services.SeriesCatalogueChoice.RecordHoldAsync(
                activity.Object, health.Object, MetadataCatalogueHold.TvdbUnavailable, $"s{index}", $"Show {index}", null, CancellationToken.None);
        }

        var note = Assert.Single(rows.Values);
        Assert.Contains("30 shows", note.Message, StringComparison.Ordinal);
        activity.Verify(repository => repository.RecordActivityAsync(
            "metadata.series.catalogue.held", It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        // A wrong list for one show still has its own note.
        await Deluno.Series.Services.SeriesCatalogueChoice.RecordHoldAsync(
            activity.Object, health.Object, MetadataCatalogueHold.TvdbDisagrees, "s99", "Other Show", null, CancellationToken.None);
        Assert.Equal(2, rows.Count);

        foreach (var index in Enumerable.Range(1, 29))
        {
            await Deluno.Series.Services.SeriesCatalogueChoice.ClearHoldAsync(health.Object, $"s{index}", CancellationToken.None);
        }

        Assert.Contains("1 show,", rows[Deluno.Series.Services.SeriesCatalogueChoice.OutageHealthId].Message, StringComparison.Ordinal);
        await Deluno.Series.Services.SeriesCatalogueChoice.ClearHoldAsync(health.Object, "s30", CancellationToken.None);
        Assert.DoesNotContain(Deluno.Series.Services.SeriesCatalogueChoice.OutageHealthId, rows.Keys);
    }

    [Fact]
    public async Task SeriesMetadataRefreshJobHandler_rejects_a_TheTVDB_list_that_is_another_shows_episodes()
    {
        var wrong = new MetadataSeason(1, null, 4, null,
        [
            new(1, 1, "Something Else", null, Now.AddYears(5), null, "tvdb"),
            new(1, 2, "Other Show", null, Now.AddYears(5).AddDays(7), null, "tvdb"),
            new(1, 3, "Unrelated", null, Now.AddYears(5).AddDays(14), null, "tvdb"),
            new(1, 4, "Nope", null, Now.AddYears(5).AddDays(21), null, "tvdb")
        ]);
        var (handler, series, activity, job) = BuildTvdbNumberedShow([wrong], [TmdbSeason()]);

        await handler.HandleAsync(job, CancellationToken.None);

        series.Verify(repository => repository.SyncEpisodeCatalogueAsync(
            It.IsAny<string>(), It.IsAny<IReadOnlyList<CatalogueEpisodeItem>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        activity.Verify(repository => repository.RecordActivityAsync(
            "metadata.series.catalogue.held",
            It.Is<string>(message => message.Contains("does not match", StringComparison.Ordinal)),
            It.IsAny<string?>(), job.Id, "series", "series-1", It.IsAny<CancellationToken>()), Times.Once);
    }
}
