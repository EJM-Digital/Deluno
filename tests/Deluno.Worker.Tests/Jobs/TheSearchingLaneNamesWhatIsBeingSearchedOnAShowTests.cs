using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Quality.Data;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Series.Services;
using Deluno.Worker.Dashboard;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>
/// #1442: a season search and an episode search, which run as jobs of their own, put their show on Live's Searching lane
/// while they run, naming what is searched ("Season 2", "S02E05"). Several at once are one card for the show, and the
/// card is gone when the last one ends.
/// </summary>
public sealed class TheSearchingLaneNamesWhatIsBeingSearchedOnAShowTests
{
    private static JobQueueItem SeasonJob(int season)
    {
        var payload = JsonSerializer.Serialize(new
        {
            seriesId = "series-1", seriesTitle = "Pioneer One", libraryId = "library-tv", seasonNumber = season,
            episodes = new[] { new { episodeId = "episode-1", episodeNumber = 1, title = "Bought" } }
        });
        return TestJobs.Create("series.search.season", payloadJson: payload, relatedEntityId: "series-1", relatedEntityType: "series");
    }

    private static JobQueueItem EpisodeJob(int season, int episode)
    {
        var payload = JsonSerializer.Serialize(new { episodeId = $"episode-{season}-{episode}", seriesId = "series-1", libraryId = "library-tv", seasonNumber = season, episodeNumber = episode, title = "Pilot" });
        return TestJobs.Create("episode.search", payloadJson: payload, relatedEntityId: $"episode-{season}-{episode}", relatedEntityType: "episode");
    }

    private static (SeasonSearchJobHandler Handler, TaskCompletionSource Release) SeasonHandler(SearchesInFlight searching)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new Mock<ISeasonSearchExecutor>();
        executor
            .Setup(item => item.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await release.Task;
                return new SeasonSearchOutcome(SeasonSearchStatus.Completed, "matched", null, 1, 1, 0, 0, 1, 1, "Pioneer.One.S01", "Example", "sent", "Sent.", null, [], ["episode-1"], true);
            });
        return (new SeasonSearchJobHandler(executor.Object, new Mock<IJobQueueRepository>().Object, searching), release);
    }

    private static (EpisodeSearchJobHandler Handler, TaskCompletionSource Release) EpisodeHandler(SearchesInFlight searching)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(item => item.ListLibrariesAsync(It.IsAny<CancellationToken>())).Returns(async () =>
        {
            await release.Task;
            throw new InvalidOperationException("The test ends the search here.");
        });
        var series = new Mock<ISeriesCatalogRepository>();
        series.Setup(item => item.GetByIdAsync("series-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(((SeriesListItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SeriesListItem))) with { Id = "series-1", Title = "Pioneer One" });
        var handler = new EpisodeSearchJobHandler(
            libraries.Object, new Mock<IQualityRepository>().Object, series.Object, new Mock<IJobQueueRepository>().Object,
            new Mock<Deluno.Integrations.Search.IAcquisitionDecisionPipeline>().Object, new Mock<Deluno.Integrations.DownloadClients.IDownloadClientGrabService>().Object,
            new Mock<IActivityFeedRepository>().Object, TimeProvider.System, searchesInFlight: searching);
        return (handler, release);
    }

    [Fact]
    public async Task A_season_search_puts_its_show_on_the_lane_as_that_season_and_takes_it_off_when_it_ends()
    {
        var searching = new SearchesInFlight(TimeProvider.System);
        var (handler, release) = SeasonHandler(searching);

        var run = handler.HandleAsync(SeasonJob(2), CancellationToken.None);
        var card = Assert.Single(searching.Snapshot());
        Assert.Equal(("series", "series-1", "Pioneer One", "Season 2"), (card.EntityType, card.EntityId, card.Title, card.Label));

        release.SetResult();
        await run;
        Assert.Empty(searching.Snapshot());
    }

    [Fact]
    public async Task An_episode_search_puts_its_show_on_the_lane_as_that_episode_and_takes_it_off_even_when_it_fails()
    {
        var searching = new SearchesInFlight(TimeProvider.System);
        var (handler, release) = EpisodeHandler(searching);

        var run = handler.HandleAsync(EpisodeJob(2, 5), CancellationToken.None);
        var card = Assert.Single(searching.Snapshot());
        Assert.Equal(("series", "series-1", "Pioneer One", "S02E05"), (card.EntityType, card.EntityId, card.Title, card.Label));

        release.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => run);
        Assert.Empty(searching.Snapshot());
    }

    [Fact]
    public async Task Several_searches_on_one_show_are_one_card_naming_each_and_it_stays_until_the_last_ends()
    {
        var searching = new SearchesInFlight(TimeProvider.System);
        var (seasonHandler, seasonRelease) = SeasonHandler(searching);
        var (episodeHandler, episodeRelease) = EpisodeHandler(searching);

        var season = seasonHandler.HandleAsync(SeasonJob(2), CancellationToken.None);
        var episode = episodeHandler.HandleAsync(EpisodeJob(3, 1), CancellationToken.None);
        var card = Assert.Single(searching.Snapshot());
        Assert.Equal("Pioneer One", card.Title);
        Assert.Equal("Season 2, S03E01", card.Label);

        seasonRelease.SetResult();
        await season;
        Assert.Equal("S03E01", Assert.Single(searching.Snapshot()).Label);

        episodeRelease.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => episode);
        Assert.Empty(searching.Snapshot());
    }
}
