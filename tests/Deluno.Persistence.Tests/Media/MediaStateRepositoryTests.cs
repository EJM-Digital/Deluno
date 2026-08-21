using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Media;

public sealed class MediaStateRepositoryTests
{
    [Theory]
    [InlineData(MediaKind.Movie)]
    [InlineData(MediaKind.Series)]
    public async Task Shared_store_preserves_wanted_state_behaviour_for_both_media_kinds(MediaKind kind)
    {
        using var storage = TestStorage.Create();
        var now = new DateTimeOffset(2026, 8, 21, 5, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(now);
        await InitializeSchemaAsync(storage, timeProvider, kind);

        var mediaId = await AddMediaAsync(storage, timeProvider, kind);
        var repository = new SqliteMediaStateRepository(storage.Factory, timeProvider);

        await repository.EnsureWantedStateAsync(
            kind,
            mediaId,
            "main",
            "missing",
            "No accepted file exists.",
            hasFile: false,
            currentQuality: null,
            targetQuality: "WEB 1080p",
            qualityCutoffMet: false,
            CancellationToken.None);

        var summary = await repository.GetWantedSummaryAsync(kind, CancellationToken.None);
        var item = Assert.Single(summary.RecentItems);
        Assert.Equal(1, summary.TotalWanted);
        Assert.Equal(mediaId, item.Id);
        Assert.Equal("missing", item.WantedStatus);

        Assert.Single(await repository.ListEligibleWantedAsync(
            kind,
            "main",
            take: 10,
            now,
            ignoreRetryWindow: false,
            CancellationToken.None));

        var deferredUntil = now.AddHours(4);
        Assert.True(await repository.DeferWantedSearchAsync(
            kind,
            mediaId,
            "main",
            deferredUntil,
            CancellationToken.None));
        Assert.Empty(await repository.ListEligibleWantedAsync(
            kind,
            "main",
            take: 10,
            now,
            ignoreRetryWindow: false,
            CancellationToken.None));

        Assert.True(await repository.SkipNextWantedSearchAsync(
            kind,
            mediaId,
            "main",
            CancellationToken.None));
        Assert.True(await repository.ConsumeSkipNextWantedSearchAsync(
            kind,
            mediaId,
            "main",
            CancellationToken.None));
        Assert.False(await repository.ConsumeSkipNextWantedSearchAsync(
            kind,
            mediaId,
            "main",
            CancellationToken.None));

        var metrics = await repository.GetDailyMetricsAsync(
            kind,
            DateOnly.FromDateTime(now.UtcDateTime),
            DateOnly.FromDateTime(now.UtcDateTime),
            CancellationToken.None);
        Assert.Equal(1, metrics.TitlesAdded[now.ToString("yyyy-MM-dd")]);
    }

    [Theory]
    [InlineData(MediaKind.Movie)]
    [InlineData(MediaKind.Series)]
    public async Task Shared_store_reads_import_recovery_summary_for_both_media_kinds(MediaKind kind)
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 8, 21, 5, 0, 0, TimeSpan.Zero));
        await InitializeSchemaAsync(storage, timeProvider, kind);

        if (kind == MediaKind.Movie)
        {
            var repository = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
            await repository.AddImportRecoveryCaseAsync(
                new CreateMovieImportRecoveryCaseRequest(
                    "Movie",
                    "quality",
                    "Below cutoff",
                    "Review quality"),
                CancellationToken.None);
        }
        else
        {
            var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
            await repository.AddImportRecoveryCaseAsync(
                new CreateSeriesImportRecoveryCaseRequest(
                    "Series",
                    "unmatched",
                    "No match",
                    "Review the title"),
                CancellationToken.None);
        }

        var summary = await new SqliteMediaStateRepository(storage.Factory, timeProvider)
            .GetImportRecoverySummaryAsync(kind, CancellationToken.None);

        Assert.Equal(1, summary.OpenCount);
        Assert.Single(summary.RecentCases);
        Assert.Equal(kind == MediaKind.Movie ? 1 : 0, summary.QualityCount);
        Assert.Equal(kind == MediaKind.Series ? 1 : 0, summary.UnmatchedCount);
    }

    private static async Task InitializeSchemaAsync(
        TestStorage storage,
        TimeProvider timeProvider,
        MediaKind kind)
    {
        var migrator = new SqliteDatabaseMigrator(storage.Factory, timeProvider);
        if (kind == MediaKind.Movie)
        {
            await new MoviesSchemaInitializer(
                storage.Factory,
                migrator,
                NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        }
        else
        {
            await new SeriesSchemaInitializer(
                storage.Factory,
                migrator,
                NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        }
    }

    private static async Task<string> AddMediaAsync(
        TestStorage storage,
        TimeProvider timeProvider,
        MediaKind kind)
    {
        if (kind == MediaKind.Movie)
        {
            var movie = await new SqliteMovieCatalogRepository(storage.Factory, timeProvider).AddAsync(
                new CreateMovieRequest("Shared movie", 2026, "tt0000001"),
                CancellationToken.None);
            return movie.Id;
        }

        var series = await new SqliteSeriesCatalogRepository(storage.Factory, timeProvider).AddAsync(
            new CreateSeriesRequest("Shared series", 2026, "tt0000002"),
            CancellationToken.None);
        return series.Id;
    }
}
