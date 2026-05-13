using Deluno.Integrations.Search;
using Microsoft.Extensions.Configuration;

namespace Deluno.Integrations.Tests.Search;

public sealed class BoundedReleaseRankingModelServiceTests
{
    [Fact]
    public void Score_returns_disabled_when_flag_is_off()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var service = new BoundedReleaseRankingModelService(configuration);

        var result = service.Score(new ReleaseRankingFeatures(
            Seeders: 40,
            SizeBytes: 4L * 1024 * 1024 * 1024,
            QualityDelta: 2,
            CustomFormatScore: 20,
            SourcePriorityScore: 100,
            EstimatedBitrateMbps: 5,
            ReleaseAgeHours: 2), hardBlocked: false);

        Assert.False(result.Enabled);
        Assert.Equal(0, result.BoostPoints);
    }

    [Fact]
    public void Score_applies_bounded_boost_when_enabled()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Deluno:RankingModel:Enabled"] = "true",
                ["Deluno:RankingModel:MaxAbsoluteBoost"] = "20"
            })
            .Build();
        var service = new BoundedReleaseRankingModelService(configuration);

        var result = service.Score(new ReleaseRankingFeatures(
            Seeders: 80,
            SizeBytes: 8L * 1024 * 1024 * 1024,
            QualityDelta: 3,
            CustomFormatScore: 50,
            SourcePriorityScore: 120,
            EstimatedBitrateMbps: 7,
            ReleaseAgeHours: 1), hardBlocked: false);

        Assert.True(result.Enabled);
        Assert.True(result.Applied);
        Assert.InRange(result.BoostPoints, 1, 20);
    }

    [Fact]
    public void Score_does_not_apply_when_hard_blocked()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Deluno:RankingModel:Enabled"] = "true"
            })
            .Build();
        var service = new BoundedReleaseRankingModelService(configuration);

        var result = service.Score(new ReleaseRankingFeatures(
            Seeders: 100,
            SizeBytes: 10L * 1024 * 1024 * 1024,
            QualityDelta: 2,
            CustomFormatScore: 70,
            SourcePriorityScore: 120,
            EstimatedBitrateMbps: 10,
            ReleaseAgeHours: 0.5), hardBlocked: true);

        Assert.True(result.Enabled);
        Assert.False(result.Applied);
        Assert.Equal(0, result.BoostPoints);
    }
}
