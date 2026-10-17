using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// Coverage register, area show-detail: the anime scene-numbering refresh (system task <c>scene.mapping.refresh</c>) is considered soon
/// after Deluno starts, runs once, is not run again until its declared twelve hours are up, and its outcome is what System > Tasks
/// reads (the same <c>worker_schedule_state</c> row). Real SQLite job store, fake clock.
/// </summary>
public sealed class TheSceneMappingRefreshRunsOnItsOwnScheduleTests
{
    private sealed class CountingRefresh(bool fail = false) : ISceneMappingRefreshService
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);

        public Task<SceneMappingRefreshSummary> RefreshAllAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            if (fail) throw new InvalidOperationException("TheXEM did not answer.");
            return Task.FromResult(new SceneMappingRefreshSummary(3, 1, 2, 17));
        }
    }

    private static async Task<SqliteJobStore> CreateStoreAsync(TestStorage storage, TimeProvider time)
    {
        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, time),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return new SqliteJobStore(storage.Factory, time, new RecordingRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
    }

    /// <summary>Moves the clock ten seconds at a time, up to <paramref name="limit"/>, until the refresh has run <paramref name="count"/> times.</summary>
    private static async Task<bool> AdvanceUntilAsync(FakeTimeProvider time, CountingRefresh refresh, int count, TimeSpan limit)
    {
        var step = limit > TimeSpan.FromMinutes(30) ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(30);
        for (var moved = TimeSpan.Zero; moved < limit; moved += step)
        {
            time.Advance(step);
            await Task.Delay(10); // reason: lets the background service run after each fake-clock step; it has no signal to await, and the loop is bounded by limit
            if (refresh.Count >= count) return true;
        }

        return refresh.Count >= count;
    }

    [Fact]
    public async Task The_refresh_runs_soon_after_start_once_and_only_again_when_its_twelve_hours_are_up_and_System_Tasks_reads_the_outcome()
    {
        using var storage = TestStorage.Create();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T08:00:00Z"));
        var store = await CreateStoreAsync(storage, time);
        var refresh = new CountingRefresh();
        var service = new SceneMappingRefreshHostedService(refresh, store, NullLogger<SceneMappingRefreshHostedService>.Instance, time);

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await AdvanceUntilAsync(time, refresh, 1, TimeSpan.FromMinutes(5)), "The refresh did not run within minutes of start.");
            await WaitForOutcomeAsync(store, "completed");

            var state = (await store.ListSystemTaskStatesAsync(CancellationToken.None)).Single(item => item.ScheduleKey == SystemTasks.SceneMappingRefresh);
            Assert.Equal("completed", state.LastResult);
            Assert.Equal(TimeSpan.FromHours(12).TotalSeconds, state.NextRunUtc!.Value.Subtract(state.LastCompletedUtc!.Value).TotalSeconds, precision: 0);
            Assert.Contains(SystemTasks.All, task => task.Key == SystemTasks.SceneMappingRefresh);

            Assert.False(await AdvanceUntilAsync(time, refresh, 2, TimeSpan.FromHours(11)), "The refresh ran again before its twelve hours were up.");
            Assert.True(await AdvanceUntilAsync(time, refresh, 2, TimeSpan.FromHours(2)), "The refresh did not run again once due.");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_refresh_that_fails_is_recorded_as_failed_and_not_tried_again_until_it_is_due()
    {
        using var storage = TestStorage.Create();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T08:00:00Z"));
        var store = await CreateStoreAsync(storage, time);
        var refresh = new CountingRefresh(fail: true);
        var service = new SceneMappingRefreshHostedService(refresh, store, NullLogger<SceneMappingRefreshHostedService>.Instance, time);

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await AdvanceUntilAsync(time, refresh, 1, TimeSpan.FromMinutes(5)));
            await WaitForOutcomeAsync(store, "failed");

            var state = (await store.ListSystemTaskStatesAsync(CancellationToken.None)).Single(item => item.ScheduleKey == SystemTasks.SceneMappingRefresh);
            Assert.Equal("failed", state.LastResult);
            Assert.False(await AdvanceUntilAsync(time, refresh, 2, TimeSpan.FromHours(6)), "A failed refresh was retried at once instead of at its next due time.");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitForOutcomeAsync(SqliteJobStore store, string result)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var states = await store.ListSystemTaskStatesAsync(CancellationToken.None);
            if (states.Any(item => item.ScheduleKey == SystemTasks.SceneMappingRefresh && item.LastResult == result)) return;
            await Task.Delay(25); // reason: the outcome is written by the background service after the refresh returns; bounded to five seconds
        }
    }
}
