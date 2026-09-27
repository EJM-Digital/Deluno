using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// The aggregated counts behind GET /api/stats (#827): the same facts the
/// Activity feed already writes in plain words, counted for a time window so
/// an external dashboard widget (or Deluno's own Dashboard) can poll them.
/// </summary>
public sealed class ActivityStatsTests
{
    private static async Task<(SqliteJobStore Store, FixedTimeProvider TimeProvider)> CreateAsync(DateTimeOffset now)
    {
        var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(now);
        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return (new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository()), timeProvider);
    }

    [Fact]
    public async Task Counts_each_bucket_from_the_categories_activity_already_writes()
    {
        var (store, timeProvider) = await CreateAsync(DateTimeOffset.Parse("2026-09-27T12:00:00Z"));

        await store.RecordActivityAsync("library.search.executed", "Searched", null, null, null, null, CancellationToken.None);
        await store.RecordActivityAsync("series.search.episode", "Searched an episode", null, null, null, null, CancellationToken.None);
        await store.RecordActivityAsync("movie.release.grabbed", "Grabbed", null, null, null, null, CancellationToken.None);
        await store.RecordActivityAsync("series.release.force-grabbed", "Force grabbed", null, null, null, null, CancellationToken.None);
        await store.RecordActivityAsync("filesystem.import.completed", "Imported", null, null, null, null, CancellationToken.None);
        await store.RecordActivityAsync("filesystem.import.season-pack.completed", "Imported a pack", null, null, null, null, CancellationToken.None);
        await store.RecordActivityAsync("download.orphan.removed", "Removed an orphan", null, null, null, null, CancellationToken.None);
        await store.RecordActivityAsync("download.sharing.reclaimed", "Reclaimed", null, null, null, null, CancellationToken.None);
        await store.RecordActivityAsync("retention.removed", "Retention removed a title", null, null, null, null, CancellationToken.None);
        // Noise: never counted in any bucket.
        await store.RecordActivityAsync("health.resolved", "Health resolved", null, null, null, null, CancellationToken.None);

        var stats = await store.GetActivityStatsAsync(timeProvider.GetUtcNow().AddHours(-24), 24, CancellationToken.None);

        Assert.Equal(2, stats.SearchesRun);
        Assert.Equal(2, stats.DownloadsStarted);
        Assert.Equal(2, stats.ImportsCompleted);
        Assert.Equal(2, stats.CleanupRemovals);
        Assert.Equal(1, stats.RetentionRemovals);
        Assert.Equal(24, stats.WindowHours);
    }

    [Fact]
    public async Task Only_counts_activity_inside_the_window()
    {
        var now = DateTimeOffset.Parse("2026-09-27T12:00:00Z");
        var (store, timeProvider) = await CreateAsync(now);

        await store.RecordActivityAsync("retention.removed", "Old", null, null, null, null, CancellationToken.None);

        timeProvider.Advance(TimeSpan.FromHours(25));
        await store.RecordActivityAsync("retention.removed", "New", null, null, null, null, CancellationToken.None);

        var stats = await store.GetActivityStatsAsync(timeProvider.GetUtcNow().AddHours(-24), 24, CancellationToken.None);

        Assert.Equal(1, stats.RetentionRemovals);
    }

    [Fact]
    public async Task An_empty_window_reports_zeroes_rather_than_failing()
    {
        var (store, timeProvider) = await CreateAsync(DateTimeOffset.Parse("2026-09-27T12:00:00Z"));

        var stats = await store.GetActivityStatsAsync(timeProvider.GetUtcNow().AddHours(-24), 24, CancellationToken.None);

        Assert.Equal(0, stats.SearchesRun);
        Assert.Equal(0, stats.DownloadsStarted);
        Assert.Equal(0, stats.ImportsCompleted);
        Assert.Equal(0, stats.CleanupRemovals);
        Assert.Equal(0, stats.RetentionRemovals);
    }
}
