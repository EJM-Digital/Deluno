using Deluno.Infrastructure.Storage;
using Deluno.Integrations.Search;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Deluno.Integrations.Tests.Search;

public sealed class MlNetReleaseRankingModelServiceTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "deluno-ml-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TrainAsync_loads_model_and_updates_status()
    {
        var service = CreateService(
            enabled: true,
            autoDispatchImpact: true,
            rows: BuildTrainingRows(180));

        var result = await service.TrainAsync("unit-test", CancellationToken.None);
        var status = service.GetStatus();

        Assert.True(result.Success);
        Assert.NotNull(result.ModelVersion);
        Assert.True(status.ModelLoaded);
        Assert.Equal(result.ModelVersion, status.ActiveModelVersion);
        Assert.True(status.TrainingSampleCount >= 100);
        Assert.NotNull(status.LastAuc);
        Assert.True(result.Auc is > 0.6, $"Expected offline AUC > 0.6 but was {result.Auc:0.###}");
    }

    [Fact]
    public async Task Score_respects_hard_block_even_with_trained_model()
    {
        var service = CreateService(
            enabled: true,
            autoDispatchImpact: true,
            rows: BuildTrainingRows(160));

        await service.TrainAsync("unit-test", CancellationToken.None);
        var result = service.Score(new ReleaseRankingFeatures(
            Seeders: 90,
            SizeBytes: 8L * 1024 * 1024 * 1024,
            QualityDelta: 2,
            CustomFormatScore: 80,
            SourcePriorityScore: 100,
            EstimatedBitrateMbps: 7.2,
            ReleaseAgeHours: 1), hardBlocked: true);

        Assert.True(result.Enabled);
        Assert.False(result.Applied);
        Assert.Equal(0, result.BoostPoints);
    }

    [Fact]
    public async Task Score_stays_offline_when_auto_dispatch_impact_disabled()
    {
        var service = CreateService(
            enabled: true,
            autoDispatchImpact: false,
            rows: BuildTrainingRows(150));

        await service.TrainAsync("unit-test", CancellationToken.None);
        var result = service.Score(new ReleaseRankingFeatures(
            Seeders: 110,
            SizeBytes: 10L * 1024 * 1024 * 1024,
            QualityDelta: 3,
            CustomFormatScore: 100,
            SourcePriorityScore: 130,
            EstimatedBitrateMbps: 9,
            ReleaseAgeHours: 0.5), hardBlocked: false);

        Assert.True(result.Enabled);
        Assert.False(result.Applied);
        Assert.Equal(0, result.BoostPoints);
        Assert.Contains("offline mode", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rollback_accepts_known_version()
    {
        var service = CreateService(
            enabled: true,
            autoDispatchImpact: true,
            rows: BuildTrainingRows(170));

        var trained = await service.TrainAsync("unit-test", CancellationToken.None);
        Assert.True(trained.Success);
        Assert.NotNull(trained.ModelVersion);

        var rolledBack = service.TryRollback(trained.ModelVersion!, out var message);

        Assert.True(rolledBack);
        Assert.Contains("Rolled back", message, StringComparison.OrdinalIgnoreCase);
    }

    private MlNetReleaseRankingModelService CreateService(
        bool enabled,
        bool autoDispatchImpact,
        IReadOnlyList<ReleaseRankingTrainingRow> rows)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Deluno:RankingModel:Enabled"] = enabled ? "true" : "false",
                ["Deluno:RankingModel:AutoDispatchImpactEnabled"] = autoDispatchImpact ? "true" : "false",
                ["Deluno:RankingModel:MaxAbsoluteBoost"] = "18",
                ["Deluno:RankingModel:MinTrainingSamples"] = "40"
            })
            .Build();

        Directory.CreateDirectory(_dataRoot);
        var options = Options.Create(new StoragePathOptions { DataRoot = _dataRoot });
        var dataSource = new FakeTrainingDataSource(rows);
        return new MlNetReleaseRankingModelService(
            configuration,
            dataSource,
            options,
            TimeProvider.System);
    }

    private static IReadOnlyList<ReleaseRankingTrainingRow> BuildTrainingRows(int count)
    {
        var random = new Random(4210);
        var rows = new List<ReleaseRankingTrainingRow>(count);
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < count; i++)
        {
            var seeders = random.Next(1, 140);
            var qualityDelta = random.Next(-1, 4);
            var customFormat = random.Next(-30, 140);
            var decisionScore = qualityDelta * 20 + customFormat / 4 + seeders / 3;
            var label = qualityDelta >= 1 && seeders >= 20 && customFormat >= 0;

            rows.Add(new ReleaseRankingTrainingRow(
                Seeders: seeders,
                SizeBytes: random.NextInt64(700_000_000L, 14_000_000_000L),
                QualityDelta: qualityDelta,
                CustomFormatScore: customFormat,
                SeederScore: seeders / 2,
                SizeScore: random.Next(-15, 40),
                DecisionScore: decisionScore,
                DecisionStatus: label ? "preferred" : "held",
                DecisionQuality: "WEB 1080p",
                ReleaseGroup: label ? "good-group" : "bad-group",
                EstimatedBitrateMbps: random.NextDouble() * 10.0,
                CreatedUtc: now.AddHours(-random.Next(1, 200)),
                GrabAttemptedUtc: now.AddHours(-random.Next(1, 160)),
                OverrideUsed: false,
                Label: label));
        }

        return rows;
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    private sealed class FakeTrainingDataSource(IReadOnlyList<ReleaseRankingTrainingRow> rows) : IReleaseRankingTrainingDataSource
    {
        public Task<IReadOnlyList<ReleaseRankingTrainingRow>> ListTrainingRowsAsync(
            int maxRows,
            DateTimeOffset? sinceUtc,
            CancellationToken cancellationToken)
        {
            return Task.FromResult((IReadOnlyList<ReleaseRankingTrainingRow>)rows.Take(maxRows).ToArray());
        }
    }
}
