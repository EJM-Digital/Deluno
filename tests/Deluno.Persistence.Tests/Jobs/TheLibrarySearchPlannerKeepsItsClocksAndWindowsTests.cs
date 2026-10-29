using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// The scheduled library search planner (coverage register, engines), on a real SQLite job store and a clock the test moves:
/// the time-of-day window (wrapping midnight) holds a due search until it opens and moves both clocks to the opening; a manual
/// request bypasses the window and the interval and is a combined, "manual" job; with only one of missing or upgrade switched on
/// the job is planned on its own dedupe key and only that clock moves.
/// </summary>
public sealed class TheLibrarySearchPlannerKeepsItsClocksAndWindowsTests
{
    private static readonly DateTimeOffset Morning = DateTimeOffset.Parse("2026-05-14T10:00:00Z");

    private static LibraryAutomationPlanItem Library(
        bool missing = true,
        bool upgrade = true,
        int? windowStart = null,
        int? windowEnd = null,
        int intervalMinutes = 12)
        => new(
            LibraryId: "films",
            LibraryName: "Films",
            MediaType: "movies",
            AutoSearchEnabled: true,
            MissingSearchEnabled: missing,
            UpgradeSearchEnabled: upgrade,
            SearchIntervalMinutes: intervalMinutes,
            RetryDelayHours: 6,
            MaxItemsPerRun: 10,
            SearchWindowStartHour: windowStart,
            SearchWindowEndHour: windowEnd);

    private static async Task<(SqliteJobStore Store, FixedTimeProvider Time)> StartAsync(DateTimeOffset now)
    {
        var storage = TestStorage.Create();
        var time = new FixedTimeProvider(now);
        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, time),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return (new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository()), time);
    }

    private static async Task<LibraryAutomationStateItem> StateAsync(SqliteJobStore store)
        => Assert.Single((await store.ListLibraryAutomationStatesAsync(CancellationToken.None)).Values);

    [Fact]
    public async Task A_search_that_falls_due_outside_the_window_waits_for_it_to_open_and_both_clocks_move_to_the_opening()
    {
        var (store, time) = await StartAsync(Morning);
        var library = Library(windowStart: 22, windowEnd: 4);

        await store.PlanLibrarySearchesAsync([library], CancellationToken.None);

        Assert.Empty(await store.ListAsync(20, CancellationToken.None));
        var held = await StateAsync(store);
        var opening = DateTimeOffset.Parse("2026-05-14T22:00:00Z");
        Assert.Equal(opening, held.NextMissingSearchUtc);
        Assert.Equal(opening, held.NextUpgradeSearchUtc);

        time.Advance(TimeSpan.FromHours(12));
        await store.PlanLibrarySearchesAsync([library], CancellationToken.None);

        var job = Assert.Single(await store.ListAsync(20, CancellationToken.None));
        Assert.Contains("\"triggeredBy\":\"schedule\"", job.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"searchKind\":\"combined\"", job.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_window_that_does_not_wrap_opens_the_next_morning_when_it_has_closed_for_the_day()
    {
        var (store, _) = await StartAsync(DateTimeOffset.Parse("2026-05-14T20:00:00Z"));

        await store.PlanLibrarySearchesAsync([Library(windowStart: 9, windowEnd: 17)], CancellationToken.None);

        Assert.Empty(await store.ListAsync(20, CancellationToken.None));
        Assert.Equal(DateTimeOffset.Parse("2026-05-15T09:00:00Z"), (await StateAsync(store)).NextMissingSearchUtc);
    }

    [Fact]
    public async Task A_manual_request_ignores_the_window_and_the_interval_and_plans_one_combined_manual_job()
    {
        var (store, time) = await StartAsync(DateTimeOffset.Parse("2026-05-14T22:00:00Z"));
        var library = Library(windowStart: 22, windowEnd: 4);

        // Inside the window the scheduled pass queues one job; skipping it leaves both clocks twelve minutes away.
        await store.PlanLibrarySearchesAsync([library], CancellationToken.None);
        Assert.True(await store.SkipLibrarySearchCycleAsync(library, CancellationToken.None));
        Assert.Empty(await store.ListAsync(20, CancellationToken.None));

        // Now outside the window and nowhere near due: only a manual request gets through.
        time.Advance(TimeSpan.FromHours(12));
        await store.PlanLibrarySearchesAsync([library], CancellationToken.None);
        Assert.Empty(await store.ListAsync(20, CancellationToken.None));

        Assert.True(await store.RequestLibrarySearchAsync(library, CancellationToken.None));
        await store.PlanLibrarySearchesAsync([library], CancellationToken.None);

        var job = Assert.Single(await store.ListAsync(20, CancellationToken.None));
        Assert.Contains("\"triggeredBy\":\"manual\"", job.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"searchKind\":\"combined\"", job.PayloadJson, StringComparison.Ordinal);
        Assert.Equal("library.search:films", job.DedupeKey);
    }

    [Fact]
    public async Task With_only_one_kind_switched_on_the_job_has_its_own_dedupe_key_and_only_its_own_clock_moves()
    {
        var (store, time) = await StartAsync(Morning);

        await store.PlanLibrarySearchesAsync([Library(upgrade: false)], CancellationToken.None);

        var job = Assert.Single(await store.ListAsync(20, CancellationToken.None));
        Assert.Equal("library.search:films:missing", job.DedupeKey);
        Assert.Contains("\"searchKind\":\"missing\"", job.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"checkUpgrades\":false", job.PayloadJson, StringComparison.Ordinal);
        var state = await StateAsync(store);
        Assert.Equal(time.GetUtcNow().AddMinutes(12), state.NextMissingSearchUtc);
        Assert.Null(state.NextUpgradeSearchUtc);
    }

    [Fact]
    public async Task With_only_upgrades_switched_on_the_job_is_an_upgrade_job_on_the_upgrade_key()
    {
        var (store, time) = await StartAsync(Morning);

        await store.PlanLibrarySearchesAsync([Library(missing: false)], CancellationToken.None);

        var job = Assert.Single(await store.ListAsync(20, CancellationToken.None));
        Assert.Equal("library.search:films:upgrade", job.DedupeKey);
        Assert.Contains("\"checkMissing\":false", job.PayloadJson, StringComparison.Ordinal);
        var state = await StateAsync(store);
        Assert.Equal(time.GetUtcNow().AddMinutes(12), state.NextUpgradeSearchUtc);
        Assert.Null(state.NextMissingSearchUtc);
    }
}
