using Deluno.Infrastructure.Storage;
using Deluno.Integrations.Search;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Deluno.Integrations.Tests.Search;

/// <summary>
/// Training reads its history within limits (coverage register, engines): the look-back is 14 to 730 days (120 by default), the row cap
/// is 500 to 1,000,000 (15,000 by default), and fewer labelled rows than the minimum is a plain refusal that trains nothing. Refs #1296.
/// </summary>
public sealed class TheRankingModelAsksForItsTrainingDataWithinLimitsTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "deluno-ml-limit-tests", Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null, null, 120, 15000)]
    [InlineData("1", "10", 14, 500)]
    [InlineData("99999", "99999999", 730, 1000000)]
    [InlineData("45", "2500", 45, 2500)]
    public async Task The_look_back_and_the_row_cap_are_clamped_before_the_history_is_asked_for(string? lookback, string? rows, int expectedDays, int expectedRows)
    {
        var source = new CapturingSource([]);
        var service = Create(source, new Dictionary<string, string?>
        {
            ["Deluno:RankingModel:TrainingLookbackDays"] = lookback,
            ["Deluno:RankingModel:MaxTrainingRows"] = rows
        });

        await service.TrainAsync("unit-test", CancellationToken.None);

        Assert.Equal(expectedRows, source.MaxRows);
        Assert.Equal(_now.AddDays(-expectedDays), source.SinceUtc);
    }

    [Fact]
    public async Task Fewer_labelled_rows_than_the_minimum_is_refused_in_plain_words_and_trains_and_stores_nothing()
    {
        var source = new CapturingSource([.. Enumerable.Range(0, 59).Select(_ => Row())]);
        var service = Create(source, []);

        var result = await service.TrainAsync("unit-test", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("Not enough labeled training rows (59/60).", result.Message);
        Assert.Equal(59, result.SampleCount);
        Assert.Null(result.ModelVersion);
        Assert.False(service.GetStatus().ModelLoaded);
        Assert.Empty(Directory.EnumerateFiles(_dataRoot, "*.zip", SearchOption.AllDirectories));
    }

    private MlNetReleaseRankingModelService Create(IReleaseRankingTrainingDataSource source, Dictionary<string, string?> extra)
    {
        var settings = new Dictionary<string, string?> { ["Deluno:RankingModel:Enabled"] = "true" };
        foreach (var pair in extra)
        {
            settings[pair.Key] = pair.Value;
        }

        Directory.CreateDirectory(_dataRoot);
        return new MlNetReleaseRankingModelService(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            source,
            Options.Create(new StoragePathOptions { DataRoot = _dataRoot }),
            new FixedClock(_now));
    }

    private ReleaseRankingTrainingRow Row()
        => new(10, 1_000_000_000L, 1, 10, 5, 5, 40, "preferred", "WEB 1080p", "grp", 5.0, _now, _now, false, true);

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    private sealed class CapturingSource(IReadOnlyList<ReleaseRankingTrainingRow> rows) : IReleaseRankingTrainingDataSource
    {
        public int MaxRows { get; private set; }

        public DateTimeOffset? SinceUtc { get; private set; }

        public Task<IReadOnlyList<ReleaseRankingTrainingRow>> ListTrainingRowsAsync(int maxRows, DateTimeOffset? sinceUtc, CancellationToken cancellationToken)
        {
            MaxRows = maxRows;
            SinceUtc = sinceUtc;
            return Task.FromResult(rows);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
