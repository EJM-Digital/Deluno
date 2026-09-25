using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Quality.Data;
using Deluno.Quality.ReleasePreferences;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Series.Services;
using Moq;

namespace Deluno.Series.Tests.Services;

/// <summary>
/// #653: a season-level search does not filter single-episode releases out of
/// its candidates (<c>AcquisitionDecisionPipeline.LeaveEpisodesToTheirOwnSearch</c>
/// deliberately skips that whenever <c>SeasonNumber</c> is set, so a real pack
/// is still scored against them). That means the planner's best answer for a
/// season can be one episode's release when no pack exists at all. Grabbing it
/// used to stand in as "the season's result" - proven live on Pioneer One: the
/// single-episode grab failed, its replacement search lifted the retry window
/// for the whole series (entity type "series"), and the next season down the
/// list grabbed its own single episode the same way, cascading through every
/// season within a minute. <see cref="SeasonSearchExecutor"/> now treats a
/// non-pack candidate as no candidate at all.
/// </summary>
public sealed class SeasonSearchExecutorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-25T04:59:15Z");

    [Fact]
    public async Task A_single_episode_release_is_not_grabbed_as_the_seasons_result()
    {
        var world = new World("Pioneer.One.S01E05.1080p.WEB-DL");

        var result = await world.Executor.ExecuteAsync("series-1", 1, "automatic-season", "was searched.", CancellationToken.None);

        Assert.False(result.Dispatched);
        Assert.Equal("checked", result.Outcome);
        world.Grabs.Verify(grab => grab.GrabAsync(It.IsAny<string>(), It.IsAny<DownloadClientGrabRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        world.Jobs.Verify(jobs => jobs.RecordDownloadDispatchAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>(), It.IsAny<IntegrationFailure?>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<DispatchReplacementTarget>?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task A_genuine_season_pack_is_still_grabbed()
    {
        var world = new World("Pioneer.One.S01.1080p.WEB-DL");

        var result = await world.Executor.ExecuteAsync("series-1", 1, "automatic-season", "was searched.", CancellationToken.None);

        Assert.True(result.Dispatched);
        Assert.Equal("matched", result.Outcome);
        world.Grabs.Verify(grab => grab.GrabAsync("client-1", It.IsAny<DownloadClientGrabRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>One series with one season of two missing episodes, a search that finds one release, and a client that takes it.</summary>
    private sealed class World
    {
        public Mock<ISeriesCatalogRepository> Repository { get; } = new();
        public Mock<IMediaStateRepository> MediaState { get; } = new();
        public Mock<ILibrariesRepository> Libraries { get; } = new();
        public Mock<IQualityRepository> Quality { get; } = new();
        public Mock<IJobQueueRepository> Jobs { get; } = new();
        public Mock<IAcquisitionDecisionPipeline> Pipeline { get; } = new();
        public Mock<IDownloadClientGrabService> Grabs { get; } = new();
        public Mock<IActivityFeedRepository> ActivityFeed { get; } = new();
        public Mock<IMediaTagStore> TagStore { get; } = new();
        public SeasonSearchExecutor Executor { get; }

        public World(string bestCandidateReleaseName)
        {
            var series = new SeriesListItem(
                "series-1", "Pioneer One", 2010, null, true, false,
                null, null, null, null, null, null, null, [], null, null, null, null, Now, Now);
            Repository.Setup(repository => repository.GetByIdAsync("series-1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(series);

            var episodes = new[]
            {
                Episode("episode-1", 1),
                Episode("episode-2", 2),
            };
            Repository.Setup(repository => repository.GetInventoryDetailAsync("series-1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SeriesInventoryDetail("series-1", "Pioneer One", 2010, 1, 2, 0, episodes));
            Repository.Setup(repository => repository.GetEpisodeFilePathAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string?)null);
            Repository.Setup(repository => repository.RecordSearchAttemptAsync(
                    It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                    It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            MediaState.Setup(state => state.ListWantedByIdsAsync(MediaKind.Series, new[] { "series-1" }, It.IsAny<CancellationToken>()))
                .ReturnsAsync([new MediaWantedItem(
                    "series-1", "Pioneer One", 2010, null, "library-tv", "missing", "missing", false,
                    null, "WEB 1080p", false, null, null, null, null, false, null, Now)]);

            var library = new LibraryItem(
                Id: "library-tv", Name: "TV", MediaType: "tv", Purpose: "Shows",
                RootPath: @"D:\Media\TV", DownloadsPath: null, QualityProfileId: null, QualityProfileName: null,
                CutoffQuality: "WEB 1080p", UpgradeUntilCutoff: true, UpgradeUnknownItems: true,
                ImportWorkflow: "direct", ProcessorName: null, ProcessorOutputPath: null, ProcessorTimeoutMinutes: 60,
                ProcessorFailureMode: "hold", AutoSearchEnabled: true, MissingSearchEnabled: true, UpgradeSearchEnabled: true,
                SearchIntervalHours: 6, RetryDelayHours: 12, MaxItemsPerRun: 50, SearchWindowStartHour: null,
                SearchWindowEndHour: null, AutomationStatus: "active", SearchRequested: false, LastSearchedUtc: null,
                NextSearchUtc: null, CreatedUtc: DateTimeOffset.UnixEpoch, UpdatedUtc: DateTimeOffset.UnixEpoch);
            Libraries.Setup(repository => repository.ListLibrariesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([library]);
            Libraries.Setup(repository => repository.GetLibraryRoutingAsync("library-tv", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new LibraryRoutingSnapshot(
                    "library-tv",
                    "TV",
                    [new LibrarySourceLinkItem("source-1", "library-tv", "indexer-1", "Fixture", 1, "", "", Now, Now)],
                    [new LibraryDownloadClientLinkItem("link-1", "library-tv", "client-1", "SABnzbd", 1, Now, Now)]));

            TagStore.Setup(store => store.ListAsync(MediaKind.Series, "series-1", It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);

            var candidate = new MediaSearchCandidate(
                bestCandidateReleaseName, "indexer-1", "Fixture", "WEB 1080p", 100, true, "Best.",
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
                    new DownloadClientGrabRequest(bestCandidateReleaseName, "http://fixture.invalid/release.nzb", "tv", "tv", "Fixture"),
                    []));
            Grabs.Setup(grab => grab.GrabAsync(It.IsAny<string>(), It.IsAny<DownloadClientGrabRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DownloadClientGrabResult("client-1", bestCandidateReleaseName, true, "sent", "sent") { ExternalId = "nzo-1" });

            Jobs.Setup(jobs => jobs.RecordDownloadDispatchAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<string?>(),
                    It.IsAny<CancellationToken>(), It.IsAny<IntegrationFailure?>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<string?>(),
                    It.IsAny<IReadOnlyList<DispatchReplacementTarget>?>(), It.IsAny<string?>()))
                .ReturnsAsync("dispatch-1");

            Executor = new SeasonSearchExecutor(
                Repository.Object,
                MediaState.Object,
                Libraries.Object,
                Quality.Object,
                new Mock<IReleasePreferencePlanRepository>().Object,
                Jobs.Object,
                Pipeline.Object,
                Grabs.Object,
                ActivityFeed.Object,
                new FixedClock(Now),
                TagStore.Object);
        }

        private static SeriesEpisodeInventoryItem Episode(string id, int number)
            => new(
                id, 1, number, $"Episode {number}", null, Now.AddDays(-30), Monitored: true, HasFile: false,
                WantedStatus: "missing", WantedReason: "missing", QualityCutoffMet: false, CurrentQuality: null,
                TargetQuality: "WEB 1080p", PreventLowerQualityReplacements: false, LastQualityDeltaDecision: null,
                LastSearchUtc: null, NextEligibleSearchUtc: null, UpdatedUtc: Now);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
