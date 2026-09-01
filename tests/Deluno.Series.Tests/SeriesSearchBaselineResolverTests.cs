using Deluno.Contracts;
using Deluno.Media;
using Deluno.Integrations.Search;
using Deluno.Quality.ReleasePreferences;
using Deluno.Series.Data;
using Moq;

namespace Deluno.Series.Tests;

public sealed class SeriesSearchBaselineResolverTests
{
    [Fact]
    public async Task ResolveEpisodeAsync_uses_exact_episode_file_and_library_snapshot()
    {
        var seriesRepository = new Mock<ISeriesCatalogRepository>(MockBehavior.Strict);
        var mediaStateRepository = new Mock<IMediaStateRepository>(MockBehavior.Strict);
        const string filePath = @"D:\TV\Example\Season 01\Example.S01E02.mkv";
        const long fileSize = 4_200_000_000;
        var evaluatedUtc = DateTimeOffset.Parse("2026-09-01T05:00:00Z");
        var evaluation = new PreferenceEvaluation(
            "plan-tv",
            "7",
            "hash-7",
            PreferenceEvaluationStatus.MeetsPlan,
            hardGatesPassed: true,
            targetsMet: true,
            families: [],
            reasons: []);
        var snapshot = new PreferenceEvaluationSnapshot(
            "series-1",
            "library-tv",
            filePath,
            filePath,
            fileSize,
            "plan-tv",
            "7",
            "hash-7",
            [],
            evaluation,
            [],
            evaluatedUtc,
            "file-probe");

        seriesRepository.Setup(repository => repository.GetEpisodeFilePathAsync("episode-2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(filePath);
        seriesRepository.Setup(repository => repository.GetEpisodeFileSizeBytesAsync("episode-2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(fileSize);
        seriesRepository.Setup(repository => repository.GetEpisodeCurrentQualityAsync("episode-2", It.IsAny<CancellationToken>()))
            .ReturnsAsync("WEB 1080p");
        seriesRepository.Setup(repository => repository.GetEpisodeTargetQualityAsync("episode-2", "library-tv", It.IsAny<CancellationToken>()))
            .ReturnsAsync("Bluray 1080p");
        mediaStateRepository.Setup(repository => repository.GetLatestPreferenceEvaluationSnapshotAsync(
                MediaKind.Series,
                "series-1",
                "library-tv",
                null,
                It.IsAny<CancellationToken>(),
                filePath,
                fileSize))
            .ReturnsAsync(snapshot);

        var baseline = await SeriesSearchBaselineResolver.ResolveEpisodeAsync(
            seriesRepository.Object,
            mediaStateRepository.Object,
            "series-1",
            "episode-2",
            "library-tv",
            CancellationToken.None);

        Assert.Equal(filePath, baseline.FilePath);
        Assert.Equal(fileSize, baseline.FileSizeBytes);
        Assert.Equal("WEB 1080p", baseline.CurrentQuality);
        Assert.Equal("Bluray 1080p", baseline.TargetQuality);
        Assert.Same(snapshot, baseline.PreferenceEvaluation);
        seriesRepository.VerifyAll();
        mediaStateRepository.VerifyAll();
    }

    [Fact]
    public async Task ListInstalledEpisodeIdsAsync_identifies_partly_installed_season_without_aggregate_guessing()
    {
        var seriesRepository = new Mock<ISeriesCatalogRepository>(MockBehavior.Strict);
        seriesRepository.Setup(repository => repository.GetEpisodeFilePathAsync("episode-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(@"D:\TV\Example\Season 01\Example.S01E01.mkv");
        seriesRepository.Setup(repository => repository.GetEpisodeFilePathAsync("episode-2", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        seriesRepository.Setup(repository => repository.GetEpisodeFilePathAsync("episode-3", It.IsAny<CancellationToken>()))
            .ReturnsAsync(@"D:\TV\Example\Season 01\Example.S01E03.mkv");

        var installed = await SeriesSearchBaselineResolver.ListInstalledEpisodeIdsAsync(
            seriesRepository.Object,
            ["episode-1", "episode-2", "episode-3"],
            CancellationToken.None);

        Assert.Equal(["episode-1", "episode-3"], installed);
        seriesRepository.VerifyAll();
    }

    [Fact]
    public void EvaluateSeasonPackCandidate_authorizes_only_when_every_installed_episode_is_a_same_plan_upgrade()
    {
        var plan = ReleasePreferencePlanFactory.CreateQualityPlan(
            "tv",
            "WEB 2160p",
            ["WEB 2160p", "WEB 1080p"],
            id: "plan-tv",
            version: "1");
        var candidate = new MediaSearchCandidate(
            "Example.Show.S01.2160p.WEB-DL",
            "indexer-1",
            "Indexer",
            "WEB 2160p",
            0,
            true,
            "candidate");
        var first = Installed("episode-1", @"D:\TV\Example\S01E01.mkv", "WEB 1080p", plan);
        var second = Installed("episode-2", @"D:\TV\Example\S01E02.mkv", "WEB 1080p", plan);

        var authorized = SeriesSearchBaselineResolver.EvaluateSeasonPackCandidate(
            plan,
            candidate,
            [first, second]);

        Assert.True(authorized.Authorized, authorized.Reason);
        Assert.Equal(2, authorized.Targets.Count);
        Assert.All(authorized.Comparisons, item => Assert.Equal(PreferenceCandidateStatus.Upgrade, item.Comparison.Status));

        var lateral = SeriesSearchBaselineResolver.EvaluateSeasonPackCandidate(
            plan,
            candidate,
            [first, Installed("episode-2", second.FilePath, "WEB 2160p", plan)]);

        Assert.False(lateral.Authorized);
        Assert.Empty(lateral.Targets);
        Assert.Contains(lateral.Comparisons, item => item.Comparison.Status == PreferenceCandidateStatus.Equivalent);
    }

    [Fact]
    public void EvaluateSeasonPackCandidate_rejects_stale_installed_evidence()
    {
        var plan = ReleasePreferencePlanFactory.CreateQualityPlan(
            "tv",
            "WEB 2160p",
            ["WEB 2160p", "WEB 1080p"],
            id: "plan-tv",
            version: "2");
        var stalePlan = ReleasePreferencePlanFactory.CreateQualityPlan(
            "tv",
            "WEB 2160p",
            ["WEB 2160p", "WEB 1080p"],
            id: "plan-tv",
            version: "1");
        var candidate = new MediaSearchCandidate(
            "Example.Show.S01.2160p.WEB-DL",
            "indexer-1",
            "Indexer",
            "WEB 2160p",
            0,
            true,
            "candidate");

        var decision = SeriesSearchBaselineResolver.EvaluateSeasonPackCandidate(
            plan,
            candidate,
            [Installed("episode-1", @"D:\TV\Example\S01E01.mkv", "WEB 1080p", stalePlan)]);

        Assert.False(decision.Authorized);
        Assert.Empty(decision.Targets);
        Assert.Contains("current plan", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static SeasonPackInstalledEpisode Installed(
        string episodeId,
        string path,
        string quality,
        ReleasePreferencePlan plan)
    {
        var facts = ReleasePreferenceFactFactory.FromReleaseName(plan, path, quality, "test");
        return new SeasonPackInstalledEpisode(
            episodeId,
            path,
            new PreferenceEvaluationSnapshot(
                "series-1",
                "library-tv",
                path,
                path,
                1234,
                plan.Id,
                plan.Version,
                plan.PlanHash,
                facts,
                ReleasePreferenceEvaluator.Evaluate(plan, facts),
                [],
                DateTimeOffset.Parse("2026-09-01T05:00:00Z"),
                "file-probe"));
    }
}
