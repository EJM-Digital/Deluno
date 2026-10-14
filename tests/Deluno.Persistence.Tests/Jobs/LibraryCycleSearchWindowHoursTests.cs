using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// A library's time-of-day search window, on a real jobs database: no scheduled search is queued outside it, including a window
/// that crosses midnight, the start hour is inside it and the end hour is not, and a library with no window searches at any hour.
/// </summary>
public sealed class LibraryCycleSearchWindowHoursTests
{
    [Theory]
    // A daytime window, 01:00 to 05:00.
    [InlineData(1, 5, 0, false)]
    [InlineData(1, 5, 1, true)]
    [InlineData(1, 5, 3, true)]
    [InlineData(1, 5, 4, true)]
    [InlineData(1, 5, 5, false)]
    [InlineData(1, 5, 12, false)]
    [InlineData(1, 5, 23, false)]
    // A window that crosses midnight, 22:00 to 06:00.
    [InlineData(22, 6, 21, false)]
    [InlineData(22, 6, 22, true)]
    [InlineData(22, 6, 23, true)]
    [InlineData(22, 6, 0, true)]
    [InlineData(22, 6, 5, true)]
    [InlineData(22, 6, 6, false)]
    [InlineData(22, 6, 12, false)]
    public async Task A_scheduled_search_is_queued_only_inside_the_hour_window(int startHour, int endHour, int nowHour, bool queued)
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 8, 27, nowHour, 30, 0, TimeSpan.Zero));
        var store = await CreateStoreAsync(storage, time);

        await store.PlanLibrarySearchesAsync([Library(startHour, endHour)], CancellationToken.None);

        var jobs = await SearchJobsAsync(store);
        Assert.Equal(queued ? 1 : 0, jobs.Count);
        // The database file really holds what the planner decided.
        Assert.Equal(queued ? 1L : 0L, SearchJobRowsOnDisk(storage));
    }

    [Fact]
    public async Task A_library_with_no_window_searches_at_any_hour()
    {
        foreach (var hour in new[] { 0, 3, 12, 23 })
        {
            using var storage = TestStorage.Create();
            var time = new FixedTimeProvider(new DateTimeOffset(2026, 8, 27, hour, 30, 0, TimeSpan.Zero));
            var store = await CreateStoreAsync(storage, time);

            await store.PlanLibrarySearchesAsync([Library(null, null)], CancellationToken.None);

            Assert.Single(await SearchJobsAsync(store));
        }
    }

    [Fact]
    public async Task A_search_held_back_by_the_window_is_queued_once_the_hour_arrives()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero));
        var store = await CreateStoreAsync(storage, time);
        var library = Library(1, 5);

        await store.PlanLibrarySearchesAsync([library], CancellationToken.None);
        Assert.Empty(await SearchJobsAsync(store));

        time.Advance(TimeSpan.FromHours(14));
        await store.PlanLibrarySearchesAsync([library], CancellationToken.None);

        Assert.Single(await SearchJobsAsync(store));
    }

    private static LibraryAutomationPlanItem Library(int? startHour, int? endHour)
        => new(
            LibraryId: "movies-main",
            LibraryName: "Movies",
            MediaType: "movies",
            AutoSearchEnabled: true,
            MissingSearchEnabled: true,
            UpgradeSearchEnabled: true,
            SearchIntervalMinutes: 6,
            RetryDelayHours: 24,
            MaxItemsPerRun: 25,
            SearchWindowStartHour: startHour,
            SearchWindowEndHour: endHour,
            MaxMissingItemsPerRun: 25,
            MaxUpgradeItemsPerRun: 25);

    private static long SearchJobRowsOnDisk(TestStorage storage)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(storage.DataRoot, "jobs.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM job_queue WHERE job_type = 'library.search.movies';";
        return (long)command.ExecuteScalar()!;
    }

    private static async Task<IReadOnlyList<JobQueueItem>> SearchJobsAsync(SqliteJobStore store)
    {
        var jobs = await store.ListAsync(100, CancellationToken.None);
        return jobs.Where(job => job.JobType == "library.search.movies").ToArray();
    }

    private static async Task<SqliteJobStore> CreateStoreAsync(TestStorage storage, TimeProvider timeProvider)
    {
        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider);
        return new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), dispatches);
    }
}
