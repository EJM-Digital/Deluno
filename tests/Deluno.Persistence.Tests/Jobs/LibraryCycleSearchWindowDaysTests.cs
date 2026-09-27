using System.Text.Json;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// A library's search window can now exclude whole days, not just hours of
/// the day (#832), and its missing/upgrade candidate caps are enforced
/// independently rather than sharing one number (#830).
/// </summary>
public sealed class LibraryCycleSearchWindowDaysTests
{
    [Fact]
    public async Task A_day_left_out_of_the_mask_skips_the_scheduled_search()
    {
        using var storage = TestStorage.Create();
        // 2026-08-27 is a Thursday.
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-27T12:00:00Z"));
        var store = await CreateStoreAsync(storage, timeProvider);
        // Every day except Thursday (bit 4).
        var library = Library(searchWindowDays: AllDays & ~(1 << 4));

        await store.PlanLibrarySearchesAsync([library], CancellationToken.None);

        Assert.Empty(await SearchJobsAsync(store));
    }

    [Fact]
    public async Task A_day_included_in_the_mask_still_searches()
    {
        using var storage = TestStorage.Create();
        // 2026-08-27 is a Thursday (bit 4).
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-27T12:00:00Z"));
        var store = await CreateStoreAsync(storage, timeProvider);
        var library = Library(searchWindowDays: 1 << 4);

        await store.PlanLibrarySearchesAsync([library], CancellationToken.None);

        Assert.Single(await SearchJobsAsync(store));
    }

    [Fact]
    public async Task An_unset_mask_means_every_day_exactly_like_before_832()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-27T12:00:00Z"));
        var store = await CreateStoreAsync(storage, timeProvider);
        var library = Library(searchWindowDays: 0);

        await store.PlanLibrarySearchesAsync([library], CancellationToken.None);

        Assert.Single(await SearchJobsAsync(store));
    }

    [Fact]
    public async Task Missing_and_upgrade_caps_are_carried_as_their_own_split_fields_on_the_queued_job()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-27T12:00:00Z"));
        var store = await CreateStoreAsync(storage, timeProvider);
        var library = Library(searchWindowDays: AllDays) with
        {
            MaxMissingItemsPerRun = 7,
            MaxUpgradeItemsPerRun = 3
        };

        await store.PlanLibrarySearchesAsync([library], CancellationToken.None);

        var job = Assert.Single(await SearchJobsAsync(store));
        using var payload = JsonDocument.Parse(job.PayloadJson!);
        Assert.Equal(7, payload.RootElement.GetProperty("maxMissingItems").GetInt32());
        Assert.Equal(3, payload.RootElement.GetProperty("maxUpgradeItems").GetInt32());
    }

    private const int AllDays = 0b111_1111;

    private static LibraryAutomationPlanItem Library(int searchWindowDays)
        => new(
            LibraryId: "movies-main",
            LibraryName: "Movies",
            MediaType: "movies",
            AutoSearchEnabled: true,
            MissingSearchEnabled: true,
            UpgradeSearchEnabled: true,
            SearchIntervalHours: 6,
            RetryDelayHours: 24,
            MaxItemsPerRun: 25,
            SearchWindowStartHour: null,
            SearchWindowEndHour: null,
            MaxMissingItemsPerRun: 25,
            MaxUpgradeItemsPerRun: 25,
            SearchWindowDays: searchWindowDays);

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

        return new SqliteJobStore(
            storage.Factory,
            timeProvider,
            new RecordingRealtimeEventPublisher(),
            new NullDownloadDispatchesRepository());
    }
}
