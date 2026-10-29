using Deluno.Infrastructure.Storage;
using Deluno.Integrations.Search;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Deluno.Integrations.Tests.Search;

/// <summary>The low-severity ranking model rows of #1329: what the registered ML service reports as its limits.</summary>
public sealed class MlNetRankingLowGapTests : IDisposable
{
    private readonly string _dataRoot = Directory.CreateTempSubdirectory("deluno-ml-low-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dataRoot, recursive: true); } catch (IOException) { }
    }

    private sealed class NoTrainingData : IReleaseRankingTrainingDataSource
    {
        public Task<IReadOnlyList<ReleaseRankingTrainingRow>> ListTrainingRowsAsync(int maxRows, DateTimeOffset? sinceUtc, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ReleaseRankingTrainingRow>>([]);
    }

    private MlNetReleaseRankingModelService Service(string? maxBoost)
    {
        var settings = new Dictionary<string, string?>();
        if (maxBoost is not null)
        {
            settings["Deluno:RankingModel:MaxAbsoluteBoost"] = maxBoost;
        }

        return new MlNetReleaseRankingModelService(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            new NoTrainingData(),
            Options.Create(new StoragePathOptions { DataRoot = _dataRoot }),
            TimeProvider.System);
    }

    [Theory]
    [InlineData(null, 20)]
    [InlineData("0", 1)]
    [InlineData("-5", 1)]
    [InlineData("1", 1)]
    [InlineData("500", 500)]
    [InlineData("1000", 1000)]
    [InlineData("5000", 1000)]
    public void The_max_boost_is_clamped_to_1_to_1000_and_defaults_to_20_on_the_registered_service(string? configured, int expected)
        => Assert.Equal(expected, Service(configured).GetStatus().MaxAbsoluteBoost);
}
