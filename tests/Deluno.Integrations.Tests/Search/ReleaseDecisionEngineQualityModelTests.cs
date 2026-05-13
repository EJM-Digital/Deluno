using Deluno.Integrations.Search;
using Deluno.Platform.Quality;

namespace Deluno.Integrations.Tests.Search;

public sealed class ReleaseDecisionEngineQualityModelTests
{
    [Fact]
    public void Decide_uses_structured_quality_model_size_bounds()
    {
        var model = new QualityModelSnapshot(
            Version: "test",
            Tiers:
            [
                new QualityTierDefinition("WEB 1080p", 70, 1.0, 2.0, 350, 1200, 50)
            ],
            UpgradeStop: new QualityUpgradeStopPolicy(true, true),
            UpdatedUtc: DateTimeOffset.UtcNow);

        var decision = ReleaseDecisionEngine.Decide(new ReleaseDecisionInput(
            ReleaseName: "Example.Release.1080p.WEB-DL-GROUP",
            Quality: "WEB 1080p",
            CurrentQuality: "WEB 720p",
            TargetQuality: "WEB 1080p",
            SizeBytes: 20L * 1024 * 1024 * 1024, // 20 GB
            Seeders: 20,
            DownloadUrl: "https://example.com/release",
            SourcePriorityScore: 100,
            CustomFormatScore: 0), model);

        Assert.Contains(decision.RiskFlags, flag => flag.Contains("unusually large", StringComparison.OrdinalIgnoreCase));
        Assert.True(decision.SizeScore < 0);
    }

    [Fact]
    public void Decide_honors_upgrade_stop_policy_when_cutoff_met()
    {
        var model = new QualityModelSnapshot(
            Version: "test",
            Tiers:
            [
                new QualityTierDefinition("WEB 1080p", 70, 1.0, 20.0, 350, 3000, 50)
            ],
            UpgradeStop: new QualityUpgradeStopPolicy(true, true),
            UpdatedUtc: DateTimeOffset.UtcNow);

        var decision = ReleaseDecisionEngine.Decide(new ReleaseDecisionInput(
            ReleaseName: "Example.Release.1080p.WEB-DL-GROUP",
            Quality: "WEB 1080p",
            CurrentQuality: "WEB 1080p",
            TargetQuality: "WEB 1080p",
            SizeBytes: 4L * 1024 * 1024 * 1024,
            Seeders: 12,
            DownloadUrl: "https://example.com/release",
            SourcePriorityScore: 100,
            CustomFormatScore: 10,
            NeverGrabPatterns: null,
            CurrentCustomFormatScore: 10), model);

        Assert.Equal("rejected", decision.Status);
        Assert.Contains(decision.RiskFlags, flag => flag.Contains("Upgrade stop policy", StringComparison.OrdinalIgnoreCase));
    }
}
