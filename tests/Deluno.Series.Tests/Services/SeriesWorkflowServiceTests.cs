using Deluno.Platform.Contracts;
using Deluno.Quality;
using Deluno.Series.Contracts;
using Deluno.Series.Services;
using Moq;

namespace Deluno.Series.Tests.Services;

public class SeriesWorkflowServiceTests
{
    private readonly Mock<IVersionedMediaPolicyEngine> mockPolicyEngine;
    private readonly SeriesWorkflowService service;

    // Ported/mirrored cases that need a real engine because it has no external
    // dependencies, so mocking it only tests the mock.
    private readonly SeriesWorkflowService realEngineService = new(new VersionedMediaPolicyEngine());

    public SeriesWorkflowServiceTests()
    {
        mockPolicyEngine = new Mock<IVersionedMediaPolicyEngine>();
        service = new SeriesWorkflowService(mockPolicyEngine.Object);
    }

    private static SeriesEpisodeInventoryItem Episode(
        int seasonNumber,
        int episodeNumber,
        bool monitored,
        bool hasFile) => new(
            EpisodeId: $"s{seasonNumber}e{episodeNumber}",
            SeasonNumber: seasonNumber,
            EpisodeNumber: episodeNumber,
            Title: null,
            Overview: null,
            AirDateUtc: null,
            Monitored: monitored,
            HasFile: hasFile,
            WantedStatus: "missing",
            WantedReason: string.Empty,
            QualityCutoffMet: false,
            CurrentQuality: null,
            TargetQuality: null,
            PreventLowerQualityReplacements: false,
            LastQualityDeltaDecision: null,
            LastSearchUtc: null,
            NextEligibleSearchUtc: null,
            UpdatedUtc: DateTimeOffset.UtcNow);

    // ── EvaluateEpisodeWantedStatus ──────────────────────────────────────────

    [Fact]
    public void EvaluateEpisodeWantedStatus_WithNoFile_ReturnsMissingStatus()
    {
        var result = service.EvaluateEpisodeWantedStatus(
            currentQuality: null,
            targetQuality: "WEB 1080p",
            qualityCutoffMet: false,
            upgradeUntilCutoff: false,
            upgradeUnknownItems: false);

        Assert.Equal("missing", result.WantedStatus);
    }

    [Fact]
    public void EvaluateEpisodeWantedStatus_WithFileAtCutoff_ReturnsWaitingStatus()
    {
        var result = service.EvaluateEpisodeWantedStatus(
            currentQuality: "WEB 1080p",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: true,
            upgradeUntilCutoff: false,
            upgradeUnknownItems: false);

        Assert.Equal("waiting", result.WantedStatus);
    }

    [Fact]
    public void EvaluateEpisodeWantedStatus_BelowCutoffWithUpgradeEnabled_ReturnsUpgradeStatus()
    {
        var result = service.EvaluateEpisodeWantedStatus(
            currentQuality: "WEB 720p",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: false,
            upgradeUntilCutoff: true,
            upgradeUnknownItems: false);

        Assert.Equal("upgrade", result.WantedStatus);
    }

    [Fact]
    public void EvaluateEpisodeWantedStatus_EmptyCurrentQuality_ReturnsMissing()
    {
        var result = realEngineService.EvaluateEpisodeWantedStatus(
            currentQuality: "",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: false,
            upgradeUntilCutoff: false,
            upgradeUnknownItems: false);

        Assert.Equal("missing", result.WantedStatus);
    }

    [Fact]
    public void EvaluateEpisodeWantedStatus_BelowCutoffWithUpgradeDisabled_ReturnsWaiting()
    {
        var result = realEngineService.EvaluateEpisodeWantedStatus(
            currentQuality: "WEB 720p",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: false,
            upgradeUntilCutoff: false,
            upgradeUnknownItems: false);

        Assert.Equal("waiting", result.WantedStatus);
    }

    // ── EvaluateCandidate ─────────────────────────────────────────────────────

    [Fact]
    public void EvaluateCandidate_WithNullCandidateQuality_ReturnsUnknownStatus()
    {
        var input = new EpisodeCandidateEvaluationInput(
            SeriesId: "123",
            EpisodeId: "e1",
            CurrentQuality: "WEB 720p",
            CandidateQuality: null!,
            TargetQuality: "WEB 1080p",
            UpgradeUntilCutoff: true,
            UpgradeUnknownItems: false,
            PreventLowerQualityReplacements: false,
            IsSeasonPack: false,
            Profile: null);

        var result = service.EvaluateCandidate(input);

        Assert.Equal("unknown", result.WantedStatus);
        Assert.Contains("quality could not be detected", result.Reason.ToLower());
    }

    [Fact]
    public void EvaluateCandidate_WithUpgradeCandidate_ReturnsUpgradeDecision()
    {
        mockPolicyEngine.Setup(x => x.QualityRank("WEB 720p")).Returns(40);
        mockPolicyEngine.Setup(x => x.QualityRank("WEB 1080p")).Returns(70);

        var input = new EpisodeCandidateEvaluationInput(
            SeriesId: "123",
            EpisodeId: "e1",
            CurrentQuality: "WEB 720p",
            CandidateQuality: "WEB 1080p",
            TargetQuality: "WEB 1080p",
            UpgradeUntilCutoff: true,
            UpgradeUnknownItems: false,
            PreventLowerQualityReplacements: false,
            IsSeasonPack: false,
            Profile: null);

        var result = service.EvaluateCandidate(input);

        Assert.Equal("upgrade", result.WantedStatus);
        Assert.Equal(30, result.QualityDelta);
        Assert.True(result.IsReplacementAllowed);
    }

    [Fact]
    public void EvaluateCandidate_WithDowngradeAndProtection_BlocksReplacement()
    {
        mockPolicyEngine.Setup(x => x.QualityRank("WEB 1080p")).Returns(70);
        mockPolicyEngine.Setup(x => x.QualityRank("WEB 720p")).Returns(40);

        var input = new EpisodeCandidateEvaluationInput(
            SeriesId: "123",
            EpisodeId: "e1",
            CurrentQuality: "WEB 1080p",
            CandidateQuality: "WEB 720p",
            TargetQuality: "WEB 1080p",
            UpgradeUntilCutoff: false,
            UpgradeUnknownItems: false,
            PreventLowerQualityReplacements: true,
            IsSeasonPack: false,
            Profile: null);

        var result = service.EvaluateCandidate(input);

        Assert.Equal("blocked", result.WantedStatus);
        Assert.False(result.IsReplacementAllowed);
        Assert.Equal(-30, result.QualityDelta);
    }

    [Fact]
    public void EvaluateCandidate_MissingEpisode_AllowsAnyReplacement()
    {
        mockPolicyEngine.Setup(x => x.QualityRank("WEB 720p")).Returns(40);

        var input = new EpisodeCandidateEvaluationInput(
            SeriesId: "123",
            EpisodeId: "e1",
            CurrentQuality: null,
            CandidateQuality: "WEB 720p",
            TargetQuality: "WEB 1080p",
            UpgradeUntilCutoff: true,
            UpgradeUnknownItems: false,
            PreventLowerQualityReplacements: true,
            IsSeasonPack: false,
            Profile: null);

        var result = service.EvaluateCandidate(input);

        Assert.Equal("missing", result.WantedStatus);
        Assert.True(result.IsReplacementAllowed);
    }

    // ── IsReplacementAllowed ──────────────────────────────────────────────────

    [Fact]
    public void IsReplacementAllowed_WithProtectionDisabled_ReturnsTrue()
    {
        var result = service.IsReplacementAllowed(
            currentQuality: "WEB 1080p",
            candidateQuality: "WEB 720p",
            preventLowerQualityReplacements: false);

        Assert.True(result);
    }

    [Fact]
    public void IsReplacementAllowed_WithNoCurrentFile_ReturnsTrue()
    {
        var result = service.IsReplacementAllowed(
            currentQuality: null,
            candidateQuality: "WEB 720p",
            preventLowerQualityReplacements: true);

        Assert.True(result);
    }

    [Fact]
    public void IsReplacementAllowed_ProtectionEnabled_LowerRank_ReturnsFalse()
    {
        var result = realEngineService.IsReplacementAllowed(
            currentQuality: "WEB 1080p",
            candidateQuality: "WEB 720p",
            preventLowerQualityReplacements: true);

        Assert.False(result);
    }

    [Fact]
    public void IsReplacementAllowed_ProtectionEnabled_SameRank_ReturnsTrue()
    {
        var result = realEngineService.IsReplacementAllowed(
            currentQuality: "WEB 1080p",
            candidateQuality: "WEB 1080p",
            preventLowerQualityReplacements: true);

        Assert.True(result);
    }

    [Fact]
    public void IsReplacementAllowed_ProtectionEnabled_HigherRank_ReturnsTrue()
    {
        var result = realEngineService.IsReplacementAllowed(
            currentQuality: "WEB 1080p",
            candidateQuality: "Remux 2160p",
            preventLowerQualityReplacements: true);

        Assert.True(result);
    }

    // ── CalculateQualityDelta ─────────────────────────────────────────────────

    [Fact]
    public void CalculateQualityDelta_WithValidQualities_ReturnsCorrectDelta()
    {
        mockPolicyEngine.Setup(x => x.QualityRank("WEB 720p")).Returns(40);
        mockPolicyEngine.Setup(x => x.QualityRank("WEB 1080p")).Returns(70);

        var result = service.CalculateQualityDelta("WEB 720p", "WEB 1080p", null);

        Assert.Equal(30, result);
    }

    [Fact]
    public void CalculateQualityDelta_WithNullCurrentQuality_ReturnsNull()
    {
        var result = service.CalculateQualityDelta(null, "WEB 1080p", null);

        Assert.Null(result);
    }

    [Fact]
    public void CalculateQualityDelta_WithInvalidRank_ReturnsNull()
    {
        mockPolicyEngine.Setup(x => x.QualityRank(It.IsAny<string>())).Returns(-1);

        var result = service.CalculateQualityDelta("WEB 1080p", "WEB 720p", null);

        Assert.Null(result);
    }

    [Fact]
    public void CalculateQualityDelta_RealEngine_UpgradeCandidate_ReturnsPositive()
    {
        var delta = realEngineService.CalculateQualityDelta("WEB 720p", "WEB 1080p", null);

        Assert.NotNull(delta);
        Assert.True(delta > 0, $"Expected positive delta but got {delta}");
    }

    // ── EvaluateSeasonPackStrategy ────────────────────────────────────────────

    [Fact]
    public void EvaluateSeasonPackStrategy_NoEpisodes_ReturnsFalseWithZeroCounts()
    {
        var result = service.EvaluateSeasonPackStrategy(
            seasonEpisodes: [],
            monitoredOnly: false);

        Assert.False(result.PreferSeasonPack);
        Assert.Equal(0, result.MonitoredMissingCount);
        Assert.Equal(0, result.TotalMonitoredCount);
    }

    [Fact]
    public void EvaluateSeasonPackStrategy_MonitoredOnlyWithNoneMonitored_ReturnsFalseWithZeroCounts()
    {
        var episodes = new[]
        {
            Episode(1, 1, monitored: false, hasFile: false),
            Episode(1, 2, monitored: false, hasFile: false),
        };

        var result = service.EvaluateSeasonPackStrategy(episodes, monitoredOnly: true);

        Assert.False(result.PreferSeasonPack);
        Assert.Equal(0, result.TotalMonitoredCount);
    }

    [Fact]
    public void EvaluateSeasonPackStrategy_MostlyMissing_PrefersSeasonPack()
    {
        // 4 of 5 missing = 80% >= 60% threshold.
        var episodes = new[]
        {
            Episode(1, 1, monitored: true, hasFile: false),
            Episode(1, 2, monitored: true, hasFile: false),
            Episode(1, 3, monitored: true, hasFile: false),
            Episode(1, 4, monitored: true, hasFile: false),
            Episode(1, 5, monitored: true, hasFile: true),
        };

        var result = service.EvaluateSeasonPackStrategy(episodes, monitoredOnly: true);

        Assert.True(result.PreferSeasonPack);
        Assert.Equal(4, result.MonitoredMissingCount);
        Assert.Equal(5, result.TotalMonitoredCount);
    }

    [Fact]
    public void EvaluateSeasonPackStrategy_ExactlyAtThreshold_PrefersSeasonPack()
    {
        // 3 of 5 missing = 60% == threshold (>= 0.6).
        var episodes = new[]
        {
            Episode(1, 1, monitored: true, hasFile: false),
            Episode(1, 2, monitored: true, hasFile: false),
            Episode(1, 3, monitored: true, hasFile: false),
            Episode(1, 4, monitored: true, hasFile: true),
            Episode(1, 5, monitored: true, hasFile: true),
        };

        var result = service.EvaluateSeasonPackStrategy(episodes, monitoredOnly: true);

        Assert.True(result.PreferSeasonPack);
    }

    [Fact]
    public void EvaluateSeasonPackStrategy_MostlyPresent_PrefersEpisodeByEpisode()
    {
        // 1 of 5 missing = 20% < 60% threshold.
        var episodes = new[]
        {
            Episode(1, 1, monitored: true, hasFile: false),
            Episode(1, 2, monitored: true, hasFile: true),
            Episode(1, 3, monitored: true, hasFile: true),
            Episode(1, 4, monitored: true, hasFile: true),
            Episode(1, 5, monitored: true, hasFile: true),
        };

        var result = service.EvaluateSeasonPackStrategy(episodes, monitoredOnly: true);

        Assert.False(result.PreferSeasonPack);
        Assert.Equal(1, result.MonitoredMissingCount);
        Assert.Equal(5, result.TotalMonitoredCount);
    }

    [Fact]
    public void EvaluateSeasonPackStrategy_MonitoredOnlyFalse_IncludesUnmonitoredEpisodes()
    {
        var episodes = new[]
        {
            Episode(1, 1, monitored: false, hasFile: false),
            Episode(1, 2, monitored: false, hasFile: false),
        };

        var result = service.EvaluateSeasonPackStrategy(episodes, monitoredOnly: false);

        Assert.True(result.PreferSeasonPack);
        Assert.Equal(2, result.TotalMonitoredCount);
    }
}
