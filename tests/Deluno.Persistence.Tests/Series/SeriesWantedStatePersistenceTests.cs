using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Infrastructure.Storage.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Series;

public sealed class SeriesWantedStatePersistenceTests
{
    [Fact]
    public async Task AddAsync_returns_existing_series_for_duplicate_identity()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T03:00:00Z"));

        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);

        var first = await repository.AddAsync(
            new CreateSeriesRequest(
                Title: "Severance",
                StartYear: 2022,
                ImdbId: "tt11280740",
                MetadataProvider: "tmdb",
                MetadataProviderId: "95396"),
            CancellationToken.None);

        var duplicate = await repository.AddAsync(
            new CreateSeriesRequest(
                Title: "severance",
                StartYear: 2022,
                ImdbId: "tt11280740",
                MetadataProvider: "tmdb",
                MetadataProviderId: "95396"),
            CancellationToken.None);

        Assert.Equal(first.Id, duplicate.Id);
        Assert.Single(await repository.ListAsync(CancellationToken.None));
    }

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

    [Fact]
    public async Task ReassignLibraryAsync_moves_selected_series_wanted_rows_between_libraries()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-05-14T00:00:00Z"));

        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var first = await repository.AddAsync(
            new CreateSeriesRequest("Andor", 2022, "tt9253284"),
            CancellationToken.None);
        var second = await repository.AddAsync(
            new CreateSeriesRequest("Silo", 2023, "tt14688458"),
            CancellationToken.None);
        var untouched = await repository.AddAsync(
            new CreateSeriesRequest("Shogun", 2024, "tt2788316"),
            CancellationToken.None);

        await repository.EnsureWantedStateAsync(
            first.Id,
            "tv-source",
            "missing",
            "Needs first acceptable file.",
            false,
            null,
            "WEB 1080p",
            false,
            CancellationToken.None);
        await repository.EnsureWantedStateAsync(
            second.Id,
            "tv-source",
            "missing",
            "Needs first acceptable file.",
            false,
            null,
            "WEB 1080p",
            false,
            CancellationToken.None);
        await repository.EnsureWantedStateAsync(
            untouched.Id,
            "tv-source",
            "missing",
            "Needs first acceptable file.",
            false,
            null,
            "WEB 1080p",
            false,
            CancellationToken.None);

        var moved = await repository.ReassignLibraryAsync(
            [first.Id, second.Id],
            "tv-source",
            "tv-target",
            CancellationToken.None);

        Assert.Equal(2, moved);

        var wanted = await repository.GetWantedSummaryAsync(CancellationToken.None);
        var firstWanted = wanted.RecentItems.Single(item => item.SeriesId == first.Id);
        var secondWanted = wanted.RecentItems.Single(item => item.SeriesId == second.Id);
        var untouchedWanted = wanted.RecentItems.Single(item => item.SeriesId == untouched.Id);

        Assert.Equal("tv-target", firstWanted.LibraryId);
        Assert.Equal("tv-target", secondWanted.LibraryId);
        Assert.Equal("tv-source", untouchedWanted.LibraryId);
    }
}
