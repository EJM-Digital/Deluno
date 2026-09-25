using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Movies.Data;
using Deluno.Quality.Data;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Series.Services;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>
/// #655. Chernobyl (one aired season of 5 episodes, nothing on disk) was added
/// monitored and grabbed twice within three seconds: the targeted show-level
/// check (<c>library.search.tv</c>, <c>triggeredBy: series-added</c>) searched
/// generally, landed on a season pack and dispatched it, and then queued the
/// season search #646 already plans for a fully-missing season — which
/// searched again and dispatched a second pack for the same five episodes.
///
/// <para>One search must decide a season. These assert the show-level pass
/// leaves a season the planner is about to hand its own
/// <c>series.search.season</c> job alone, for both ways a library search runs:
/// the add path (targeted at the new series, retry window ignored) and the
/// scheduled cycle (untargeted, paced by the retry window).
/// <see cref="Deluno.Series.Tests.Services.SeasonSearchExecutorTests"/> covers
/// the other half: the season job itself does not send its pack again if
/// something else already put the series on its way.</para>
/// </summary>
public sealed class OneSearchDecidesASeasonTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-25T06:10:56Z");

    [Fact]
    public async Task The_add_path_leaves_a_fully_missing_season_to_its_own_search()
    {
        var world = new World();

        await world.Handler().HandleAsync(LibraryJob(targetEntityId: "series-1", triggeredBy: "series-added"), CancellationToken.None);

        world.AssertShowLevelDeferredToSeasonSearch();
    }

    [Fact]
    public async Task The_scheduled_cycle_leaves_a_fully_missing_season_to_its_own_search()
    {
        var world = new World();

        await world.Handler().HandleAsync(LibraryJob(targetEntityId: null, triggeredBy: "schedule"), CancellationToken.None);

        world.AssertShowLevelDeferredToSeasonSearch();
    }

    private static JobQueueItem LibraryJob(string? targetEntityId, string triggeredBy)
        => TestJobs.Create("library.search.tv", payloadJson: JsonSerializer.Serialize(new
        {
            libraryId = "library-tv",
            libraryName = "TV",
            mediaType = "tv",
            checkMissing = true,
            checkUpgrades = false,
            searchKind = "missing",
            maxItems = 25,
            retryDelayHours = 12,
            triggeredBy,
            targetEntityId
        }));

    /// <summary>
    /// One series, "Chernobyl", one aired season of two due, missing,
    /// monitored episodes (the minimum #646 will plan as a season), and a
    /// general show-level search whose best answer is a season pack.
    /// </summary>
    private sealed class World
    {
        private const string SeasonPackRelease = "Chernobyl.S01.1080p.AMZN.WEB-DL.DDP5.1.x264-R";

        public Mock<ILibrariesRepository> Libraries { get; } = new();
        public Mock<IJobQueueRepository> Jobs { get; } = new();
        public Mock<ISeriesCatalogRepository> Series { get; } = new();
        public Mock<IAcquisitionDecisionPipeline> Pipeline { get; } = new();
        public Mock<IDownloadClientGrabService> Grabs { get; } = new();

        private IReadOnlyList<SeasonSearchPlanItem>? _plannedSeasons;

        public World()
        {
            Libraries.Setup(repository => repository.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
            Jobs.Setup(repository => repository.ListDownloadsInFlightAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);

            Series.Setup(repository => repository.ListEligibleWantedAsync(
                    It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<bool>(),
                    It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<CatalogueFilters?>(), It.IsAny<bool>()))
                .ReturnsAsync([new SeriesWantedItem(
                    "series-1", "Chernobyl", 2019, null, "library-tv", "missing", "missing",
                    false, null, "WEB 1080p", false, false, null, Now, null, null, null, Now)]);

            Series.Setup(repository => repository.ListEligibleWantedEpisodesAsync(
                    It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<bool>(),
                    It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<CatalogueFilters?>(), It.IsAny<bool>()))
                .ReturnsAsync([
                    new EpisodeSearchEligibilityItem("episode-1", "series-1", 1, 1, "1:23:45", null, null),
                    new EpisodeSearchEligibilityItem("episode-2", "series-1", 1, 2, "Please Remain Calm", null, null)
                ]);

            Series.Setup(repository => repository.GetInventoryDetailAsync("series-1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SeriesInventoryDetail("series-1", "Chernobyl", 2019, 1, 2, 0, [
                    Episode("episode-1", 1),
                    Episode("episode-2", 2)
                ]));

            var candidate = new MediaSearchCandidate(
                SeasonPackRelease, "indexer-1", "Fixture", "WEB 1080p", 100, true, "Best.",
                DownloadUrl: "http://fixture.invalid/release.nzb");
            Pipeline.Setup(pipeline => pipeline.PlanAsync(It.IsAny<AcquisitionDecisionRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AcquisitionDecisionPlan(
                    new MediaSearchPlan(candidate, [candidate], "Found one."),
                    Deluno.Quality.MediaPolicyCatalog.CurrentVersion,
                    "matched",
                    "Found one.",
                    1,
                    1,
                    new LibraryDownloadClientLinkItem("link-1", "library-tv", "client-1", "SABnzbd", 1, Now, Now),
                    true,
                    new DownloadClientGrabRequest(SeasonPackRelease, "http://fixture.invalid/release.nzb", "tv", "tv", "Fixture"),
                    []));
            Grabs.Setup(grab => grab.GrabAsync(It.IsAny<string>(), It.IsAny<DownloadClientGrabRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DownloadClientGrabResult("client-1", SeasonPackRelease, true, "sent", "sent") { ExternalId = "nzo-1" });

            Jobs.Setup(repository => repository.PlanSeasonSearchesAsync(
                    It.IsAny<string>(), It.IsAny<IReadOnlyList<SeasonSearchPlanItem>>(), It.IsAny<CancellationToken>()))
                .Callback<string, IReadOnlyList<SeasonSearchPlanItem>, CancellationToken>((_, seasons, _) => _plannedSeasons = seasons)
                .Returns(Task.CompletedTask);
            Jobs.Setup(repository => repository.PlanEpisodeSearchesAsync(
                    It.IsAny<string>(), It.IsAny<IReadOnlyList<EpisodeSearchPlanItem>>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        }

        private static SeriesEpisodeInventoryItem Episode(string id, int number)
            => new(
                id, 1, number, $"Episode {number}", null, Now.AddDays(-30), Monitored: true, HasFile: false,
                WantedStatus: "missing", WantedReason: "missing", QualityCutoffMet: false, CurrentQuality: null,
                TargetQuality: "WEB 1080p", PreventLowerQualityReplacements: false, LastQualityDeltaDecision: null,
                LastSearchUtc: null, NextEligibleSearchUtc: null, UpdatedUtc: Now);

        public TvLibrarySearchJobHandler Handler()
            => new(Libraries.Object, new Mock<IQualityRepository>().Object, Jobs.Object,
                new Mock<IMovieCatalogRepository>().Object, Series.Object,
                Pipeline.Object, Grabs.Object, new Mock<IActivityFeedRepository>().Object, new FixedClock(Now));

        public void AssertShowLevelDeferredToSeasonSearch()
        {
            // Exactly one decision maker: the show-level pass never sends the
            // season pack it found - it is a season #646 already plans its
            // own search for.
            Grabs.Verify(grab => grab.GrabAsync(It.IsAny<string>(), It.IsAny<DownloadClientGrabRequest>(), It.IsAny<CancellationToken>()), Times.Never);
            Jobs.Verify(jobs => jobs.RecordDownloadDispatchAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>(), It.IsAny<IntegrationFailure?>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<DispatchReplacementTarget>?>(), It.IsAny<string?>()), Times.Never);

            Assert.NotNull(_plannedSeasons);
            Assert.Contains(_plannedSeasons!, season => season.SeriesId == "series-1" && season.SeasonNumber == 1);

            Series.Verify(series => series.RecordSearchAttemptAsync(
                "series-1", null, "library-tv", It.IsAny<string>(), "skipped", It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset?>(),
                It.Is<string>(message => message.Contains("season 1")), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
