using Deluno.Infrastructure.Storage;
using Deluno.Integrations.Search;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Deluno.Integrations.Tests.Search;

/// <summary>
/// The ranking model's switches, limits and memory (coverage register, area engines): it does nothing while it is off or untrained, it refuses to train on too
/// little, every limit is held inside its range, and what it learned is still there after a restart and after a rollback.
/// </summary>
public sealed class RankingModelRefusesAndRemembersTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-15T09:00:00Z");
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "deluno-ml-refuses", Guid.NewGuid().ToString("N"));

    private sealed class RecordingSource(IReadOnlyList<ReleaseRankingTrainingRow> rows) : IReleaseRankingTrainingDataSource
    {
        public int Asked { get; private set; }

        public int? MaxRows { get; private set; }

        public DateTimeOffset? Since { get; private set; }

        public Task<IReadOnlyList<ReleaseRankingTrainingRow>> ListTrainingRowsAsync(int maxRows, DateTimeOffset? sinceUtc, CancellationToken cancellationToken)
        {
            Asked++;
            MaxRows = maxRows;
            Since = sinceUtc;
            return Task.FromResult((IReadOnlyList<ReleaseRankingTrainingRow>)rows.Take(maxRows).ToArray());
        }
    }

    private MlNetReleaseRankingModelService Service(RecordingSource source, params (string Key, string Value)[] settings)
    {
        var values = new Dictionary<string, string?>();
        foreach (var (key, value) in settings)
        {
            values[$"Deluno:RankingModel:{key}"] = value;
        }

        Directory.CreateDirectory(_dataRoot);
        return new MlNetReleaseRankingModelService(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            source,
            Options.Create(new StoragePathOptions { DataRoot = _dataRoot }),
            new FixedClock(Now, advancing: false));
    }

    /// <summary>Fixed, or (when asked) a clock that moves on a second at every look, so models trained one after the other get different versions.</summary>
    private sealed class FixedClock(DateTimeOffset now, bool advancing) : TimeProvider
    {
        private int _looks;

        public override DateTimeOffset GetUtcNow() => advancing ? now.AddSeconds(Interlocked.Increment(ref _looks)) : now;
    }

    private static IReadOnlyList<ReleaseRankingTrainingRow> Rows(int count)
    {
        var random = new Random(4210);
        var rows = new List<ReleaseRankingTrainingRow>(count);
        for (var i = 0; i < count; i++)
        {
            var seeders = random.Next(1, 140);
            var qualityDelta = random.Next(-1, 4);
            var customFormat = random.Next(-30, 140);
            var label = qualityDelta >= 1 && seeders >= 20 && customFormat >= 0;
            rows.Add(new ReleaseRankingTrainingRow(
                Seeders: seeders, SizeBytes: random.NextInt64(700_000_000L, 14_000_000_000L), QualityDelta: qualityDelta, CustomFormatScore: customFormat,
                SeederScore: seeders / 2, SizeScore: random.Next(-15, 40), DecisionScore: qualityDelta * 20 + customFormat / 4 + seeders / 3,
                DecisionStatus: label ? "preferred" : "held", DecisionQuality: "WEB 1080p", ReleaseGroup: label ? "good-group" : "bad-group",
                EstimatedBitrateMbps: random.NextDouble() * 10.0, CreatedUtc: Now.AddHours(-random.Next(1, 200)), GrabAttemptedUtc: Now.AddHours(-random.Next(1, 160)),
                OverrideUsed: false, Label: label));
        }

        return rows;
    }

    private static readonly ReleaseRankingFeatures AnyRelease = new(
        Seeders: 90, SizeBytes: 8L * 1024 * 1024 * 1024, QualityDelta: 2, CustomFormatScore: 80, SourcePriorityScore: 100, EstimatedBitrateMbps: 7.2, ReleaseAgeHours: 1);

    [Fact]
    public async Task With_the_model_off_training_is_refused_in_plain_words_asks_for_no_data_and_scoring_adds_nothing()
    {
        var source = new RecordingSource(Rows(200));
        var service = Service(source, ("Enabled", "false"));

        var trained = await service.TrainAsync("manual", CancellationToken.None);
        var scored = service.Score(AnyRelease, hardBlocked: false);

        Assert.False(trained.Success);
        Assert.Equal("Ranking model is disabled in configuration.", trained.Message);
        Assert.Equal(0, source.Asked);
        Assert.False(scored.Enabled);
        Assert.False(scored.Applied);
        Assert.Equal(0, scored.BoostPoints);
        Assert.Equal("ML ranking is disabled.", scored.Explanation);
    }

    [Fact]
    public void With_the_model_on_but_nothing_trained_scoring_adds_nothing_and_says_no_model_is_loaded()
    {
        var service = Service(new RecordingSource(Rows(200)), ("Enabled", "true"), ("AutoDispatchImpactEnabled", "true"));

        var scored = service.Score(AnyRelease, hardBlocked: false);

        Assert.True(scored.Enabled);
        Assert.False(scored.Applied);
        Assert.Equal(0, scored.BoostPoints);
        Assert.Equal("No trained ML model is loaded yet.", scored.Explanation);
        Assert.False(service.GetStatus().ModelLoaded);
    }

    [Theory]
    [InlineData(null, 10, "Not enough labeled training rows (10/60).")]
    [InlineData("40", 39, "Not enough labeled training rows (39/40).")]
    [InlineData("5", 19, "Not enough labeled training rows (19/20).")]
    public async Task Training_on_too_few_rows_is_refused_with_the_count_and_the_minimum_which_is_held_between_20_and_100000(string? configured, int rows, string message)
    {
        var settings = configured is null ? new[] { ("Enabled", "true") } : new[] { ("Enabled", "true"), ("MinTrainingSamples", configured) };
        var service = Service(new RecordingSource(Rows(rows)), settings);

        var result = await service.TrainAsync("manual", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(message, result.Message);
        Assert.Equal(rows, result.SampleCount);
        Assert.Null(result.ModelVersion);
        Assert.False(service.GetStatus().ModelLoaded);
    }

    [Fact]
    public async Task A_minimum_above_the_ceiling_is_held_at_100000()
    {
        var service = Service(new RecordingSource(Rows(30)), ("Enabled", "true"), ("MinTrainingSamples", "9999999"));

        var result = await service.TrainAsync("manual", CancellationToken.None);

        Assert.Equal("Not enough labeled training rows (30/100000).", result.Message);
    }

    [Theory]
    [InlineData(null, null, 120, 15000)]
    [InlineData("3", "10", 14, 500)]
    [InlineData("9999", "99999999", 730, 1000000)]
    [InlineData("45", "700", 45, 700)]
    public async Task Training_reads_a_lookback_between_14_and_730_days_and_a_row_cap_between_500_and_a_million_default_120_days_and_15000_rows(string? lookback, string? maxRows, int expectedDays, int expectedRows)
    {
        var source = new RecordingSource(Rows(0));
        var settings = new List<(string, string)> { ("Enabled", "true") };
        if (lookback is not null) settings.Add(("TrainingLookbackDays", lookback));
        if (maxRows is not null) settings.Add(("MaxTrainingRows", maxRows));
        var service = Service(source, settings.ToArray());

        await service.TrainAsync("manual", CancellationToken.None);

        Assert.Equal(expectedRows, source.MaxRows);
        Assert.Equal(Now.AddDays(-expectedDays), source.Since);
    }

    [Fact]
    public async Task Two_trainings_in_the_same_second_get_two_versions_and_the_first_is_not_overwritten()
    {
        var service = Service(new RecordingSource(Rows(180)), ("Enabled", "true"), ("MinTrainingSamples", "40"));

        var first = await service.TrainAsync("first", CancellationToken.None);
        var second = await service.TrainAsync("second", CancellationToken.None);

        Assert.True(first.Success, first.Message);
        Assert.True(second.Success, second.Message);
        Assert.NotEqual(first.ModelVersion, second.ModelVersion);
        var versions = service.GetStatus().AvailableVersions ?? [];
        Assert.Contains(first.ModelVersion!, versions);
        Assert.Contains(second.ModelVersion!, versions);
    }

    [Fact]
    public async Task After_a_restart_the_model_marked_active_is_loaded_again_without_training_and_a_rollback_survives_the_next_restart()
    {
        var first = Service(new RecordingSource(Rows(180)), ("Enabled", "true"), ("MinTrainingSamples", "40"), ("AutoDispatchImpactEnabled", "true"));
        var older = await first.TrainAsync("first", CancellationToken.None);
        Assert.True(older.Success, older.Message);
        var newer = await first.TrainAsync("second", CancellationToken.None);
        Assert.True(newer.Success, newer.Message);
        Assert.NotEqual(older.ModelVersion, newer.ModelVersion);
        Assert.Equal(newer.ModelVersion, first.GetStatus().ActiveModelVersion);

        var untrained = new RecordingSource(Rows(0));
        var restarted = Service(untrained, ("Enabled", "true"), ("AutoDispatchImpactEnabled", "true"));
        var status = restarted.GetStatus();

        Assert.Equal(0, untrained.Asked);
        Assert.True(status.ModelLoaded);
        Assert.Equal(newer.ModelVersion, status.ActiveModelVersion);
        Assert.Contains(older.ModelVersion!, (status.AvailableVersions ?? []));
        Assert.Contains(newer.ModelVersion!, (status.AvailableVersions ?? []));

        Assert.True(restarted.TryRollback(older.ModelVersion!, out var said));
        Assert.Equal($"Rolled back ranking model to {older.ModelVersion}.", said);
        var afterRollback = Service(new RecordingSource(Rows(0)), ("Enabled", "true")).GetStatus();
        Assert.Equal(older.ModelVersion, afterRollback.ActiveModelVersion);
    }

    [Fact]
    public async Task The_boost_is_bounded_by_the_configured_maximum_and_applied_only_when_it_rounds_to_a_non_zero_number()
    {
        var service = Service(new RecordingSource(Rows(200)), ("Enabled", "true"), ("AutoDispatchImpactEnabled", "true"), ("MaxAbsoluteBoost", "5"), ("MinTrainingSamples", "40"));
        Assert.True((await service.TrainAsync("manual", CancellationToken.None)).Success);

        var strong = service.Score(AnyRelease with { Seeders = 130, QualityDelta = 3, CustomFormatScore = 130 }, hardBlocked: false);
        var weak = service.Score(AnyRelease with { Seeders = 1, QualityDelta = -1, CustomFormatScore = -30 }, hardBlocked: false);
        var between = Enumerable.Range(0, 12).Select(step => service.Score(AnyRelease with { Seeders = 10 + step * 10, QualityDelta = step % 3, CustomFormatScore = step * 12 - 20 }, hardBlocked: false)).ToArray();

        Assert.All(new[] { strong, weak }.Concat(between), result =>
        {
            Assert.InRange(result.BoostPoints, -5, 5);
            Assert.Equal(result.BoostPoints != 0, result.Applied);
        });
    }

    [Theory]
    [InlineData("", "A target version is required.")]
    [InlineData("   ", "A target version is required.")]
    [InlineData("rm-never-trained", "Model version 'rm-never-trained' was not found.")]
    public void A_rollback_with_no_version_or_one_that_is_not_stored_is_refused_in_plain_words_and_changes_nothing(string version, string message)
    {
        var service = Service(new RecordingSource(Rows(0)), ("Enabled", "true"));

        var accepted = service.TryRollback(version, out var said);

        Assert.False(accepted);
        Assert.Equal(message, said);
        Assert.False(service.GetStatus().ModelLoaded);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }
}
