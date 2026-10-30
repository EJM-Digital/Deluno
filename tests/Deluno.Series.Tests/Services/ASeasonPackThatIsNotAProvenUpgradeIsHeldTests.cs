using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;
using Deluno.Quality.ReleasePreferences;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Series.Services;
using Moq;

namespace Deluno.Series.Tests.Services;

/// <summary>
/// #1296: a whole-season search that finds a pack for a season with installed episodes sends it only when it is a proven upgrade for every
/// installed episode. When it is not (one installed file is already as good, or the evidence is for another plan), the season search says
/// "held", records why on each episode and sends nothing: the executor never asks the download client and never records a dispatch.
/// </summary>
public sealed class ASeasonPackThatIsNotAProvenUpgradeIsHeldTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-25T04:59:15Z");
    private const string Pack = "Pioneer.One.S01.2160p.WEB-DL";
    private static readonly QualityProfileItem Profile = new(
        "profile-1", "Ultra", "tv", "WEB 2160p", "WEB 2160p,WEB 1080p", string.Empty, true, true, false, null, null, false, Now, Now);

    [Fact]
    public async Task A_pack_that_is_only_an_upgrade_for_some_installed_episodes_is_held_and_nothing_is_sent()
    {
        var world = new World(Pack, installedQualities: ["WEB 1080p", "WEB 2160p"]);

        var result = await world.Executor.ExecuteAsync("series-1", 1, "automatic-season", "was searched.", CancellationToken.None);

        Assert.False(result.Dispatched);
        Assert.Equal("held", result.Outcome);
        world.AssertNothingWasSent();
        Assert.Equal(2, world.Attempts.Count);
        Assert.All(world.Attempts, attempt => Assert.Equal("held", attempt.Outcome));
        Assert.All(world.Attempts, attempt => Assert.False(string.IsNullOrWhiteSpace(attempt.Reason)));
    }

    [Fact]
    public async Task A_pack_that_is_no_better_than_any_installed_episode_is_held_and_nothing_is_sent()
    {
        var world = new World(Pack, installedQualities: ["WEB 2160p", "WEB 2160p"]);

        var result = await world.Executor.ExecuteAsync("series-1", 1, "automatic-season", "was searched.", CancellationToken.None);

        Assert.False(result.Dispatched);
        Assert.Equal("held", result.Outcome);
        world.AssertNothingWasSent();
    }

    [Fact]
    public async Task A_pack_that_is_a_proven_upgrade_for_every_installed_episode_is_sent()
    {
        var world = new World(Pack, installedQualities: ["WEB 1080p", "WEB 1080p"]);

        var result = await world.Executor.ExecuteAsync("series-1", 1, "automatic-season", "was searched.", CancellationToken.None);

        Assert.True(result.Dispatched);
        world.Grabs.Verify(grab => grab.GrabAsync("client-1", It.IsAny<DownloadClientGrabRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private sealed class World
    {
        public Mock<IDownloadClientGrabService> Grabs { get; } = new();
        public Mock<IJobQueueRepository> Jobs { get; } = new();
        public List<(string Outcome, string? Reason)> Attempts { get; } = [];
        public SeasonSearchExecutor Executor { get; }

        public World(string releaseName, string[] installedQualities)
        {
            var plan = ReleasePreferencePlanFactory.CreateQualityPlan(Profile, []);
            var repository = new Mock<ISeriesCatalogRepository>();
            repository.Setup(r => r.GetByIdAsync("series-1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SeriesListItem("series-1", "Pioneer One", 2010, null, true, false,
                    null, null, null, null, null, null, null, [], null, null, null, null, Now, Now));
            var episodes = installedQualities.Select((_, index) => Episode($"episode-{index + 1}", index + 1)).ToArray();
            repository.Setup(r => r.GetInventoryDetailAsync("series-1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SeriesInventoryDetail("series-1", "Pioneer One", 2010, 1, episodes.Length, episodes.Length, episodes));
            repository.Setup(r => r.RecordSearchAttemptAsync(
                    It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                    It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Callback<string, string?, string, string, string, DateTimeOffset, DateTimeOffset?, string?, string?, string?, string?, CancellationToken>(
                    (_, _, _, _, outcome, _, _, reason, _, _, _, _) => Attempts.Add((outcome, reason)))
                .Returns(Task.CompletedTask);

            var mediaState = new Mock<IMediaStateRepository>();
            mediaState.Setup(s => s.ListWantedByIdsAsync(MediaKind.Series, new[] { "series-1" }, It.IsAny<CancellationToken>()))
                .ReturnsAsync([new MediaWantedItem("series-1", "Pioneer One", 2010, null, "library-tv", "upgrade", "upgrade", false,
                    null, "WEB 2160p", false, null, null, null, null, false, null, Now)]);
            for (var index = 0; index < installedQualities.Length; index++)
            {
                var id = $"episode-{index + 1}";
                var path = $"/tv/Pioneer One/S01E0{index + 1}.mkv";
                var facts = ReleasePreferenceFactFactory.FromReleaseName(plan, path, installedQualities[index], "test");
                repository.Setup(r => r.GetEpisodeFilePathAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(path);
                mediaState.Setup(s => s.GetLatestPreferenceEvaluationSnapshotAsync(
                        MediaKind.Series, "series-1", "library-tv", null, It.IsAny<CancellationToken>(), path, It.IsAny<long?>()))
                    .ReturnsAsync(new PreferenceEvaluationSnapshot("series-1", "library-tv", path, path, 1234, plan.Id, plan.Version, plan.PlanHash,
                        facts, ReleasePreferenceEvaluator.Evaluate(plan, facts), [], Now.AddDays(-1), "file-probe"));
            }

            var quality = new Mock<IQualityRepository>();
            quality.Setup(q => q.ListQualityProfilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Profile]);
            quality.Setup(q => q.ListCustomFormatsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

            var libraries = new Mock<ILibrariesRepository>();
            var library = new LibraryItem(
                Id: "library-tv", Name: "TV", MediaType: "tv", Purpose: "Shows",
                RootPath: "/tv", DownloadsPath: null, QualityProfileId: "profile-1", QualityProfileName: "Ultra",
                CutoffQuality: "WEB 2160p", UpgradeUntilCutoff: true, UpgradeUnknownItems: true,
                ImportWorkflow: "direct", ProcessorName: null, ProcessorOutputPath: null, ProcessorTimeoutMinutes: 60,
                ProcessorFailureMode: "hold", AutoSearchEnabled: true, MissingSearchEnabled: true, UpgradeSearchEnabled: true,
                SearchIntervalMinutes: 6, RetryDelayHours: 12, MaxItemsPerRun: 50, SearchWindowStartHour: null,
                SearchWindowEndHour: null, AutomationStatus: "active", SearchRequested: false, LastSearchedUtc: null,
                NextSearchUtc: null, CreatedUtc: DateTimeOffset.UnixEpoch, UpdatedUtc: DateTimeOffset.UnixEpoch);
            libraries.Setup(l => l.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([library]);
            libraries.Setup(l => l.GetLibraryRoutingAsync("library-tv", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new LibraryRoutingSnapshot("library-tv", "TV",
                    [new LibrarySourceLinkItem("source-1", "library-tv", "indexer-1", "Fixture", 1, "", "", Now, Now)],
                    [new LibraryDownloadClientLinkItem("link-1", "library-tv", "client-1", "SABnzbd", 1, Now, Now)]));

            var tags = new Mock<IMediaTagStore>();
            tags.Setup(t => t.ListAsync(MediaKind.Series, "series-1", It.IsAny<CancellationToken>())).ReturnsAsync([]);

            var candidate = new MediaSearchCandidate(releaseName, "indexer-1", "Fixture", "WEB 2160p", 100, true, "Best.",
                DownloadUrl: "http://fixture.invalid/release.nzb");
            var pipeline = new Mock<IAcquisitionDecisionPipeline>();
            pipeline.Setup(p => p.PlanAsync(It.IsAny<AcquisitionDecisionRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AcquisitionDecisionPlan(
                    new MediaSearchPlan(candidate, [candidate], "Found one."),
                    Deluno.Quality.MediaPolicyCatalog.CurrentVersion, "matched", "Found one.", 1, 1,
                    new LibraryDownloadClientLinkItem("link-1", "library-tv", "client-1", "SABnzbd", 1, Now, Now),
                    true,
                    new DownloadClientGrabRequest(releaseName, "http://fixture.invalid/release.nzb", "tv", "tv", "Fixture"),
                    []));
            pipeline.Setup(p => p.ExplainDownloadClientRoutingAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string?)null);
            Grabs.Setup(g => g.GrabAsync(It.IsAny<string>(), It.IsAny<DownloadClientGrabRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DownloadClientGrabResult("client-1", releaseName, true, "sent", "sent") { ExternalId = "nzo-1" });
            Jobs.Setup(j => j.RecordDownloadDispatchAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<string?>(),
                    It.IsAny<CancellationToken>(), It.IsAny<IntegrationFailure?>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<string?>(),
                    It.IsAny<IReadOnlyList<DispatchReplacementTarget>?>(), It.IsAny<string?>()))
                .ReturnsAsync("dispatch-1");

            Executor = new SeasonSearchExecutor(
                repository.Object, mediaState.Object, libraries.Object, quality.Object,
                new Mock<IReleasePreferencePlanRepository>().Object, Jobs.Object, pipeline.Object, Grabs.Object,
                new Mock<IActivityFeedRepository>().Object, new FixedClock(Now), tags.Object);
        }

        public void AssertNothingWasSent()
        {
            Grabs.Verify(g => g.GrabAsync(It.IsAny<string>(), It.IsAny<DownloadClientGrabRequest>(), It.IsAny<CancellationToken>()), Times.Never);
            Jobs.Verify(j => j.RecordDownloadDispatchAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>(), It.IsAny<IntegrationFailure?>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<DispatchReplacementTarget>?>(), It.IsAny<string?>()), Times.Never);
        }

        private static SeriesEpisodeInventoryItem Episode(string id, int number)
            => new(
                id, 1, number, $"Episode {number}", null, Now.AddDays(-30), Monitored: true, HasFile: true,
                WantedStatus: "upgrade", WantedReason: "upgrade", QualityCutoffMet: false, CurrentQuality: "WEB 1080p",
                TargetQuality: "WEB 2160p", PreventLowerQualityReplacements: false, LastQualityDeltaDecision: null,
                LastSearchUtc: null, NextEligibleSearchUtc: null, UpdatedUtc: Now);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
