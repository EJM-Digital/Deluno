using System.Text.Json;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Series.Services;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>
/// #646: the automatic season search runs through the same
/// <see cref="ISeasonSearchExecutor"/> the "Search season" button uses, and
/// when it ends without a grab, every episode it was planned for still gets
/// its own search — nothing is left unsearched for the next cycle to notice.
/// </summary>
public sealed class SeasonSearchJobHandlerTests
{
    private static readonly object[] Episodes =
    [
        new { episodeId = "episode-1", episodeNumber = 1, title = "Bought" },
        new { episodeId = "episode-2", episodeNumber = 2, title = "The Second Door" },
        new { episodeId = "episode-3", episodeNumber = 3, title = "Login" }
    ];

    [Fact]
    public async Task Missing_series_id_skips_without_calling_the_executor()
    {
        var seasonSearchExecutor = new Mock<ISeasonSearchExecutor>(MockBehavior.Strict);
        var jobQueueRepository = new Mock<IJobQueueRepository>(MockBehavior.Strict);
        var handler = new SeasonSearchJobHandler(seasonSearchExecutor.Object, jobQueueRepository.Object);

        var message = await handler.HandleAsync(TestJobs.Create("series.search.season", payloadJson: "not json"), CancellationToken.None);

        Assert.Equal("Finished searching for season.", message);
    }

    [Fact]
    public async Task A_dispatched_pack_leaves_nothing_to_fall_back_to()
    {
        var seasonSearchExecutor = new Mock<ISeasonSearchExecutor>();
        var jobQueueRepository = new Mock<IJobQueueRepository>(MockBehavior.Strict);

        seasonSearchExecutor
            .Setup(executor => executor.ExecuteAsync("series-1", 1, "automatic-season", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Completed(dispatched: true));

        var handler = new SeasonSearchJobHandler(seasonSearchExecutor.Object, jobQueueRepository.Object);
        var message = await handler.HandleAsync(SeasonJob(), CancellationToken.None);

        Assert.Contains("S01", message);
        // Strict mock: PlanEpisodeSearchesAsync must not have been called.
        jobQueueRepository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task No_acceptable_pack_falls_back_to_searching_every_planned_episode()
    {
        var seasonSearchExecutor = new Mock<ISeasonSearchExecutor>();
        var jobQueueRepository = new Mock<IJobQueueRepository>();

        seasonSearchExecutor
            .Setup(executor => executor.ExecuteAsync("series-1", 1, "automatic-season", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Completed(dispatched: false));

        IReadOnlyList<EpisodeSearchPlanItem>? planned = null;
        jobQueueRepository
            .Setup(repository => repository.PlanEpisodeSearchesAsync("library-tv", It.IsAny<IReadOnlyList<EpisodeSearchPlanItem>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<EpisodeSearchPlanItem>, CancellationToken>((_, episodes, _) => planned = episodes)
            .Returns(Task.CompletedTask);

        var handler = new SeasonSearchJobHandler(seasonSearchExecutor.Object, jobQueueRepository.Object);
        var message = await handler.HandleAsync(SeasonJob(), CancellationToken.None);

        Assert.Contains("no season pack was grabbed", message, StringComparison.Ordinal);
        Assert.NotNull(planned);
        Assert.Equal(["episode-1", "episode-2", "episode-3"], planned!.Select(item => item.EpisodeId));
        Assert.All(planned, item => Assert.Equal("series-1", item.SeriesId));
        Assert.All(planned, item => Assert.Equal(1, item.SeasonNumber));
    }

    [Theory]
    [InlineData(SeasonSearchStatus.SeriesNotFound)]
    [InlineData(SeasonSearchStatus.SeasonNotFound)]
    [InlineData(SeasonSearchStatus.NotSearchable)]
    [InlineData(SeasonSearchStatus.LibraryMissing)]
    public async Task A_series_or_season_that_no_longer_exists_is_not_planned_for_fallback(SeasonSearchStatus status)
    {
        var seasonSearchExecutor = new Mock<ISeasonSearchExecutor>();
        var jobQueueRepository = new Mock<IJobQueueRepository>(MockBehavior.Strict);

        seasonSearchExecutor
            .Setup(executor => executor.ExecuteAsync("series-1", 1, "automatic-season", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeasonSearchOutcome(
                status, "not-found", null, 1, 0, 0, 0, 0, 0, null, null, null, null, null, [], [], false));

        var handler = new SeasonSearchJobHandler(seasonSearchExecutor.Object, jobQueueRepository.Object);
        await handler.HandleAsync(SeasonJob(), CancellationToken.None);

        jobQueueRepository.VerifyNoOtherCalls();
    }

    // Defect: SeasonSearchExecutor records nextEligibleSearchUtc = now +
    // retryDelayHours for every episode in the season when it holds or
    // blocks — EvidenceHeld (an installed episode's evaluation is stale),
    // Blocked (no indexers/download client linked), and a Completed-but-held
    // outcome (a candidate was found but was not a safe automatic upgrade).
    // The handler used to fall back to PlanEpisodeSearchesAsync in every one
    // of these cases anyway, which plans episode.search at "now" and runs
    // straight through the backoff the executor had just written. None of
    // these three should ever reach PlanEpisodeSearchesAsync.
    [Fact]
    public async Task Evidence_held_respects_its_own_retry_window_instead_of_searching_episodes_immediately()
    {
        var seasonSearchExecutor = new Mock<ISeasonSearchExecutor>();
        var jobQueueRepository = new Mock<IJobQueueRepository>(MockBehavior.Strict);

        seasonSearchExecutor
            .Setup(executor => executor.ExecuteAsync("series-1", 1, "automatic-season", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SeasonSearchOutcome.EvidenceHeld(1, 3, 2, 1, "Whole-season replacement was held."));

        var handler = new SeasonSearchJobHandler(seasonSearchExecutor.Object, jobQueueRepository.Object);
        await handler.HandleAsync(SeasonJob(), CancellationToken.None);

        jobQueueRepository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Blocked_on_missing_routing_respects_its_own_retry_window_instead_of_searching_episodes_immediately()
    {
        var seasonSearchExecutor = new Mock<ISeasonSearchExecutor>();
        var jobQueueRepository = new Mock<IJobQueueRepository>(MockBehavior.Strict);

        seasonSearchExecutor
            .Setup(executor => executor.ExecuteAsync("series-1", 1, "automatic-season", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SeasonSearchOutcome.Blocked(1, 3, "No indexers are linked to this library yet."));

        var handler = new SeasonSearchJobHandler(seasonSearchExecutor.Object, jobQueueRepository.Object);
        await handler.HandleAsync(SeasonJob(), CancellationToken.None);

        jobQueueRepository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_candidate_not_authorized_as_an_upgrade_respects_its_own_retry_window_instead_of_searching_episodes_immediately()
    {
        var seasonSearchExecutor = new Mock<ISeasonSearchExecutor>();
        var jobQueueRepository = new Mock<IJobQueueRepository>(MockBehavior.Strict);

        // Status is Completed (the search actually ran and picked a
        // candidate), but Outcome is "held" rather than "checked" — the
        // candidate was not a proven upgrade for every installed episode, and
        // SeasonSearchExecutor recorded the same retryDelayHours backoff as
        // Blocked/EvidenceHeld for it.
        seasonSearchExecutor
            .Setup(executor => executor.ExecuteAsync("series-1", 1, "automatic-season", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeasonSearchOutcome(
                SeasonSearchStatus.Completed, "held", "Not a proven upgrade for every installed episode.",
                1, 3, 2, 0, 0, 0, "Pioneer.One.S01.1080p", "Example", null, null, null, [], ["episode-1", "episode-2", "episode-3"], false));

        var handler = new SeasonSearchJobHandler(seasonSearchExecutor.Object, jobQueueRepository.Object);
        await handler.HandleAsync(SeasonJob(), CancellationToken.None);

        jobQueueRepository.VerifyNoOtherCalls();
    }

    private static JobQueueItem SeasonJob()
    {
        var payload = JsonSerializer.Serialize(new
        {
            seriesId = "series-1",
            seriesTitle = "Pioneer One",
            libraryId = "library-tv",
            seasonNumber = 1,
            episodes = Episodes
        });
        return TestJobs.Create("series.search.season", payloadJson: payload, relatedEntityId: "series-1", relatedEntityType: "series");
    }

    private static SeasonSearchOutcome Completed(bool dispatched)
        => new(
            SeasonSearchStatus.Completed,
            dispatched ? "matched" : "checked",
            dispatched ? null : "No acceptable release was found.",
            SeasonNumber: 1,
            SearchedEpisodes: 3,
            InstalledEpisodeCount: 0,
            MissingEvidenceEpisodeCount: 0,
            MatchedCount: dispatched ? 3 : 0,
            QueuedCount: dispatched ? 1 : 0,
            ReleaseName: dispatched ? "Pioneer.One.S01.1080p" : null,
            IndexerName: dispatched ? "Example" : null,
            DispatchStatus: dispatched ? "sent" : null,
            DispatchMessage: dispatched ? "Sent to the download client." : null,
            ReplacementComparisons: null,
            Failures: [],
            SeasonEpisodeIds: ["episode-1", "episode-2", "episode-3"],
            Dispatched: dispatched);
}
