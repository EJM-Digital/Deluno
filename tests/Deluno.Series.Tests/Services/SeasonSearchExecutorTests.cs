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

    /// <summary>
    /// #1062: an automatic season pack that turns out dead is blocklisted and
    /// searched past by the grab service, so the executor hands it the series,
    /// the library and the episodes the pack covered.
    /// </summary>
    [Fact]
    public async Task An_automatic_season_pack_tells_the_grab_service_what_to_search_again_if_it_is_dead()
    {
        var world = new World("Pioneer.One.S01.1080p.WEB-DL");

        await world.Executor.ExecuteAsync("series-1", 1, "automatic-season", "was searched.", CancellationToken.None);

        world.Grabs.Verify(
            grab => grab.GrabAsync(
                "client-1",
                It.Is<DownloadClientGrabRequest>(request =>
                    request.OnDeadRelease != null
                    && request.OnDeadRelease.EntityType == "series"
                    && request.OnDeadRelease.EntityId == "series-1"
                    && request.OnDeadRelease.LibraryId != null
                    && request.OnDeadRelease.SeasonEpisodeIds != null
                    && request.OnDeadRelease.SeasonEpisodeIds.Count > 0),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// #655: Chernobyl's targeted show-level search dispatched a season pack,
    /// and the season search #646 already plans for a fully-missing season ran
    /// straight after and dispatched a second one — nothing here knew the first
    /// had happened. The season job now checks the same in-flight list every
    /// other search path checks, keyed on the series id every one of those
    /// paths dispatches under.
    /// </summary>
    [Fact]
    public async Task A_season_already_on_its_way_is_not_sent_again()
    {
        var world = new World("Pioneer.One.S01.1080p.WEB-DL");
        world.Jobs.Setup(jobs => jobs.ListDownloadsInFlightAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DownloadInFlight("series", "series-1", "Pioneer.One.S01.720p.WEB-DL")]);

        var result = await world.Executor.ExecuteAsync("series-1", 1, "automatic-season", "was searched.", CancellationToken.None);

        Assert.False(result.Dispatched);
        Assert.Equal("skipped", result.Outcome);
        world.Grabs.Verify(grab => grab.GrabAsync(It.IsAny<string>(), It.IsAny<DownloadClientGrabRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        world.Jobs.Verify(jobs => jobs.RecordDownloadDispatchAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>(), It.IsAny<IntegrationFailure?>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<DispatchReplacementTarget>?>(), It.IsAny<string?>()), Times.Never);
    }

    /// <summary>
    /// #808: the season path is the one automatic-search path that lives
    /// outside <c>Deluno.Worker</c> (it cannot reference
    /// <c>SearchExecutionSupport</c>), so it explains tag-based routing
    /// inline. Prove it reaches Activity the same way the library and
    /// episode paths do through the shared helper.
    /// </summary>
    [Fact]
    public async Task A_tag_routed_client_is_explained_in_activity()
    {
        var world = new World("Pioneer.One.S01.1080p.WEB-DL");
        world.Pipeline
            .Setup(pipeline => pipeline.ExplainDownloadClientRoutingAsync("client-1", It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Sent to SABnzbd because this series is tagged anime, which only SABnzbd accepts.");

        var result = await world.Executor.ExecuteAsync("series-1", 1, "automatic-season", "was searched.", CancellationToken.None);

        Assert.True(result.Dispatched);
        world.ActivityFeed.Verify(
            feed => feed.RecordActivityAsync(
                "series.release.grabbed",
                It.Is<string>(message => message.Contains("SABnzbd") && message.Contains("tagged anime")),
                null,
                null,
                "series",
                "series-1",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task An_untagged_client_writes_no_extra_routing_activity()
    {
        var world = new World("Pioneer.One.S01.1080p.WEB-DL");
        world.Pipeline
            .Setup(pipeline => pipeline.ExplainDownloadClientRoutingAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var result = await world.Executor.ExecuteAsync("series-1", 1, "automatic-season", "was searched.", CancellationToken.None);

        Assert.True(result.Dispatched);
        world.ActivityFeed.Verify(
            feed => feed.RecordActivityAsync(
                "series.release.grabbed",
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
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
                SearchIntervalMinutes: 6, RetryDelayHours: 12, MaxItemsPerRun: 50, SearchWindowStartHour: null,
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
            Pipeline.Setup(pipeline => pipeline.ExplainDownloadClientRoutingAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string?)null);

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
