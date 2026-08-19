using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Series.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Series;

/// <summary>
/// Finding episodes worth re-fetching must not mean reading every series and
/// every episode of each one. These pin the SQL replacement, including the
/// per-series cap that keeps one long-running show from filling the batch.
/// </summary>
public sealed class EpisodeRecoveryQueryTests
{
    [Fact]
    public async Task Recovery_candidates_are_capped_per_series_and_overall()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-19T08:00:00Z"));
        var series = await CreateRepositoryAsync(storage, timeProvider);

        // Two shows, twelve episodes each, every one below cutoff.
        await ImportShowAsync(series, "library-tv", "Long Runner", 2001, 12);
        await ImportShowAsync(series, "library-tv", "Other Show", 2010, 12);

        var candidates = await series.ListEpisodesNeedingRecoveryAsync(
            "library-tv",
            perSeriesLimit: 5,
            take: 20,
            CancellationToken.None);

        // Five from each show, not twelve from the first.
        Assert.Equal(10, candidates.Count);
        Assert.Equal(candidates.Count, candidates.Distinct().Count());

        var capped = await series.ListEpisodesNeedingRecoveryAsync(
            "library-tv",
            perSeriesLimit: 5,
            take: 6,
            CancellationToken.None);
        Assert.Equal(6, capped.Count);
    }

    [Fact]
    public async Task Recovery_candidates_are_scoped_to_the_library_that_was_asked_for()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-19T08:00:00Z"));
        var series = await CreateRepositoryAsync(storage, timeProvider);

        await ImportShowAsync(series, "library-tv", "In Scope", 2001, 3);
        await ImportShowAsync(series, "library-anime", "Out Of Scope", 2002, 3);

        var candidates = await series.ListEpisodesNeedingRecoveryAsync(
            "library-tv",
            perSeriesLimit: 5,
            take: 20,
            CancellationToken.None);

        Assert.Equal(3, candidates.Count);

        Assert.Empty(await series.ListEpisodesNeedingRecoveryAsync(
            "library-nothing-here",
            perSeriesLimit: 5,
            take: 20,
            CancellationToken.None));
    }

    [Fact]
    public async Task Recovery_priority_reads_one_episode_and_returns_zero_for_an_unknown_one()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-19T08:00:00Z"));
        var series = await CreateRepositoryAsync(storage, timeProvider);
        await ImportShowAsync(series, "library-tv", "In Scope", 2001, 2);

        var service = new EpisodeImportRecoveryService(series, timeProvider);
        var candidates = await service.FindEpisodesNeedingRecoveryAsync("library-tv", CancellationToken.None);
        Assert.Equal(2, candidates.Count);

        Assert.True(await service.RecoveryPriorityAsync(candidates[0], CancellationToken.None) > 0);
        Assert.Equal(0, await service.RecoveryPriorityAsync("no-such-episode", CancellationToken.None));
    }

    private static async Task ImportShowAsync(
        ISeriesCatalogRepository series,
        string libraryId,
        string title,
        int startYear,
        int episodeCount)
    {
        var episodes = Enumerable.Range(1, episodeCount)
            .Select(number => new ImportedEpisodeItem(
                SeasonNumber: 1,
                EpisodeNumber: number,
                HasFile: true,
                FilePath: $"D:\\Media\\{title}\\S01E{number:D2}.mkv",
                FileSizeBytes: 1024))
            .ToArray();

        await series.ImportExistingBatchAsync(
            libraryId,
            [
                new ExistingSeriesImportRequest(
                    Title: title,
                    StartYear: startYear,
                    WantedStatus: "covered",
                    WantedReason: "Imported from your existing library.",
                    CurrentQuality: "HDTV-720p",
                    TargetQuality: "WEBDL-1080p",
                    QualityCutoffMet: false,
                    UnmonitorWhenCutoffMet: false,
                    FilePath: episodes[0].FilePath,
                    FileSizeBytes: 1024,
                    Episodes: episodes)
            ],
            CancellationToken.None);
    }

    private static async Task<SqliteSeriesCatalogRepository> CreateRepositoryAsync(
        TestStorage storage,
        TimeProvider timeProvider)
    {
        var migrator = new SqliteDatabaseMigrator(storage.Factory, timeProvider);
        await new SeriesSchemaInitializer(
            storage.Factory,
            migrator,
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
    }
}
