using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Infrastructure.Storage.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Series;

public sealed class SeriesWantedStatePersistenceTests
{
    [Fact]
    public async Task EnsureWantedStateAsync_creates_and_updates_one_state_per_series_and_library()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T03:00:00Z"));

        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await repository.AddAsync(
            new CreateSeriesRequest("Severance", 2022, "tt11280740"),
            CancellationToken.None);

        await repository.EnsureWantedStateAsync(
            series.Id,
            libraryId: "series-main",
            wantedStatus: "missing",
            wantedReason: "No accepted episodes exist.",
            hasFile: false,
            currentQuality: null,
            targetQuality: "WEB 1080p",
            qualityCutoffMet: false,
            CancellationToken.None);

        await repository.EnsureWantedStateAsync(
            series.Id,
            libraryId: "series-main",
            wantedStatus: "waiting",
            wantedReason: "Current file is accepted.",
            hasFile: true,
            currentQuality: "WEB 1080p",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: true,
            CancellationToken.None);

        var summary = await repository.GetWantedSummaryAsync(CancellationToken.None);
        var item = Assert.Single(summary.RecentItems);

        Assert.Equal(1, summary.TotalWanted);
        Assert.Equal(0, summary.MissingCount);
        Assert.Equal(0, summary.UpgradeCount);
        Assert.Equal(1, summary.WaitingCount);
        Assert.Equal(series.Id, item.SeriesId);
        Assert.Equal("series-main", item.LibraryId);
        Assert.Equal("waiting", item.WantedStatus);
        Assert.True(item.HasFile);
        Assert.True(item.QualityCutoffMet);
        Assert.Equal("WEB 1080p", item.CurrentQuality);
    }
}
