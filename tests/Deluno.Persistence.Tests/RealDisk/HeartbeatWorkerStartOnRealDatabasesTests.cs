using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Worker.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// The worker's start-up on the real jobs database file: a job the previous run left leased goes back to the queue, the worker
/// writes its heartbeat, and every enabled lane gets its own supervised loop on the lane board.
/// </summary>
public sealed class HeartbeatWorkerStartOnRealDatabasesTests
{
    private static readonly string WorkerId = $"worker-{Environment.MachineName.ToLowerInvariant()}";

    [Fact]
    public async Task Starting_releases_the_leases_the_previous_run_left_writes_the_heartbeat_and_starts_a_loop_for_every_enabled_lane()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var lane = JobLanes.All.First(candidate => candidate.Enabled);
        var left = await app.Services.GetRequiredService<IJobScheduler>().EnqueueAsync(
            new EnqueueJobRequest("filesystem.import.execute", "test", "{\"left\":true}", null, null), CancellationToken.None);
        var someoneElses = await app.Services.GetRequiredService<IJobScheduler>().EnqueueAsync(
            new EnqueueJobRequest("filesystem.import.execute", "test", "{\"other\":true}", null, null), CancellationToken.None);
        var until = DateTimeOffset.UtcNow.AddMinutes(10).ToString("O");
        await ApiScenes.ExecuteAsync(app, "jobs",
            $"UPDATE job_queue SET status = 'running', started_utc = '{DateTimeOffset.UtcNow:O}', leased_until_utc = '{until}', worker_id = '{WorkerId}-{lane.Name}' WHERE id = '{left.Id}';" +
            $"UPDATE job_queue SET status = 'running', started_utc = '{DateTimeOffset.UtcNow:O}', leased_until_utc = '{until}', worker_id = 'worker-another-machine-{lane.Name}' WHERE id = '{someoneElses.Id}';");
        var board = new JobLaneStateBoard(TimeProvider.System);
        var worker = new DelunoHeartbeatWorker(
            NullLogger<DelunoHeartbeatWorker>.Instance,
            app.Services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            app.Services.GetRequiredService<IJobLaneSignal>(),
            board);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await Wait.UntilAsync(
                async () => await ApiScenes.ScalarAsync(app, "jobs", $"SELECT COUNT(*) FROM worker_heartbeats WHERE worker_id = '{WorkerId}';") == 1
                    && JobLanes.All.Where(candidate => candidate.Enabled).All(candidate => board.Snapshot().Any(reading => reading.Name == candidate.Name)),
                "the worker to write its heartbeat and start its lanes",
                TimeSpan.FromSeconds(30));

            Assert.Equal("queued", await ApiScenes.TextAsync(app, "jobs", $"SELECT status FROM job_queue WHERE id = '{left.Id}';"));
            Assert.True(string.IsNullOrEmpty(await ApiScenes.TextAsync(app, "jobs", $"SELECT worker_id FROM job_queue WHERE id = '{left.Id}';")));
            Assert.Equal("running", await ApiScenes.TextAsync(app, "jobs", $"SELECT status FROM job_queue WHERE id = '{someoneElses.Id}';"));
            Assert.True(File.Exists(Path.Combine(app.DataRoot, "jobs.db")));
            var onTheBoard = board.Snapshot().Select(reading => reading.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.All(JobLanes.All.Where(candidate => candidate.Enabled), candidate => Assert.Contains(candidate.Name, onTheBoard));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }
}
