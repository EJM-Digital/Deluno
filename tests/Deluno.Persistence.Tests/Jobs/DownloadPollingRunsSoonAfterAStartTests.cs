using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// #601: the download polling pass is considered soon after Deluno starts and
/// runs when it is due, however often Deluno restarts.
///
/// <para><b>Why this file exists.</b> The pass waited on an hour-long timer
/// whose first tick came an hour after start. On the soak rig Deluno was
/// redeployed about ten times in a day, each within the hour, and the pass
/// that archives imported downloads and finds stuck ones never ran: twenty
/// imported downloads sat unarchived.</para>
/// </summary>
public sealed class DownloadPollingRunsSoonAfterAStartTests
{
    [Fact]
    public async Task The_pass_runs_within_a_minute_of_start_and_then_only_when_due()
    {
        using var storage = TestStorage.Create();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-23T14:00:00Z"));
        var store = await CreateStoreAsync(storage, time);
        var polling = new CountingPolling();
        var service = new DownloadDispatchPollingHostedService(NullLogger<DownloadDispatchPollingHostedService>.Instance, polling, store, time);

        await service.StartAsync(CancellationToken.None);
        try
        {
            // The service starts on its own thread, so the clock moves in
            // small steps rather than one jump it might set its timer after.
            Assert.True(await AdvanceUntilAsync(time, polling, 1, TimeSpan.FromMinutes(1)), "The pass did not run within a minute of start.");

            // Checked every few minutes, but not due again for an hour.
            Assert.False(await AdvanceUntilAsync(time, polling, 2, TimeSpan.FromMinutes(30)), "The pass ran again before it was due.");
            Assert.True(await AdvanceUntilAsync(time, polling, 2, TimeSpan.FromMinutes(40)), "The pass did not run again once due.");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Moves the clock ten seconds at a time, up to <paramref name="limit"/>, until the pass has run <paramref name="count"/> times.</summary>
    private static async Task<bool> AdvanceUntilAsync(FakeTimeProvider time, CountingPolling polling, int count, TimeSpan limit)
    {
        for (var moved = TimeSpan.Zero; moved < limit; moved += TimeSpan.FromSeconds(10))
        {
            time.Advance(TimeSpan.FromSeconds(10));
            await Task.Delay(20); // reason: lets the background polling task run after each fake-clock step; it has no signal to await, and the loop is bounded by limit
            if (polling.Count >= count)
            {
                return true;
            }
        }

        return polling.Count >= count;
    }

    private static async Task<SqliteJobStore> CreateStoreAsync(TestStorage storage, TimeProvider time)
    {
        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, time),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return new SqliteJobStore(storage.Factory, time, new RecordingRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
    }

    private sealed class CountingPolling : IDownloadDispatchPollingService
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public Task<DownloadDispatchPollingReport> PollAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            return Task.FromResult(new DownloadDispatchPollingReport(0, 0, 0, 0, 0, 0));
        }
    }
}
