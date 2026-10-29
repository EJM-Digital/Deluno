using System.Net.Http.Json;
using System.Reflection;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Worker.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Worker;

/// <summary>
/// One tick of an executor lane, on the real job store: it leases a batch for two minutes, runs each job in a scope of its
/// own and completes it with the handler's message, sleeps until the lane's next job is due when it leased nothing, reads
/// the automatic-jobs switch at most every fifteen seconds, and writes its liveness heartbeat at most every fifteen seconds
/// whatever the switch says.
/// </summary>
public sealed class TheLaneTickLeasesRunsAndSleepsTests
{
    private static readonly string WorkerId = $"worker-{Environment.MachineName.ToLowerInvariant()}";

    private sealed record Rig(ApplicationTestHost App, DelunoHeartbeatWorker Worker, FixedTimeProvider Clock) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();

        public Task<LaneTickResult> TickAsync(JobLane lane)
            => (Task<LaneTickResult>)typeof(DelunoHeartbeatWorker)
                .GetMethod("RunLaneTickAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(Worker, [lane, 4, CancellationToken.None])!;

        public Task RunJobAsync(JobLane lane, JobQueueItem job)
            => (Task)typeof(DelunoHeartbeatWorker)
                .GetMethod("RunJobAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(Worker, [lane, job, CancellationToken.None])!;

        public async Task SwitchAsync(bool on)
        {
            using var response = await App.Client.PutAsJsonAsync("/api/settings/automation", new { isEnabled = on });
            response.EnsureSuccessStatusCode();
        }

        public async Task<JobQueueItem> EnqueueAsync(string jobType, DateTimeOffset? scheduled = null)
            => await App.Services.GetRequiredService<IJobScheduler>().EnqueueAsync(
                new EnqueueJobRequest(jobType, "test", "{\"n\":\"" + Guid.NewGuid().ToString("N") + "\"}", null, null, scheduled), CancellationToken.None);

        public Task<string?> HeartbeatAsync()
            => ApiScenes.TextAsync(App, "jobs", $"SELECT last_seen_utc FROM worker_heartbeats WHERE worker_id = '{WorkerId}';");
    }

    private static async Task<Rig> StartAsync()
    {
        var app = await ApplicationTestHost.StartAsync();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var worker = new DelunoHeartbeatWorker(
            NullLogger<DelunoHeartbeatWorker>.Instance,
            app.Services.GetRequiredService<IServiceScopeFactory>(),
            clock,
            app.Services.GetRequiredService<IJobLaneSignal>());
        return new Rig(app, worker, clock);
    }

    private static JobLane Lane(string name) => JobLanes.All.Single(lane => lane.Name == name);

    [Fact]
    public async Task A_lane_with_work_leases_it_for_two_minutes()
    {
        await using var rig = await StartAsync();
        await rig.SwitchAsync(true);
        var job = await rig.EnqueueAsync("filesystem.import.execute");

        var tick = await rig.TickAsync(Lane("import.files"));

        var leased = Assert.Single(tick.Jobs);
        Assert.Equal(job.Id, leased.Id);
        var left = leased.LeasedUntilUtc!.Value - DateTimeOffset.UtcNow;
        Assert.InRange(left.TotalSeconds, 100, 125);
        Assert.Equal($"{WorkerId}-import.files", leased.WorkerId);
    }

    [Fact]
    public async Task A_lane_with_nothing_due_sleeps_until_its_own_next_job_is_due_and_not_longer()
    {
        await using var rig = await StartAsync();
        await rig.SwitchAsync(true);
        await rig.EnqueueAsync("filesystem.import.execute", DateTimeOffset.UtcNow.AddSeconds(40));
        await rig.EnqueueAsync("library.media.probe", DateTimeOffset.UtcNow.AddSeconds(5));

        var tick = await rig.TickAsync(Lane("import.files"));

        Assert.Empty(tick.Jobs);
        Assert.InRange(tick.SleepFor.TotalSeconds, 30, 41);
    }

    [Fact]
    public async Task A_leased_job_runs_in_a_scope_of_its_own_and_completes_with_the_handlers_message_in_Activity()
    {
        await using var rig = await StartAsync();
        await rig.SwitchAsync(true);
        var job = await rig.EnqueueAsync("library.media.probe");
        var lane = Lane("media.probe");

        var tick = await rig.TickAsync(lane);
        await rig.RunJobAsync(lane, Assert.Single(tick.Jobs));

        Assert.Equal("completed", (await rig.App.Services.GetRequiredService<IJobQueueRepository>().GetAsync(job.Id, CancellationToken.None))!.Status);
        var line = (await rig.App.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(50, null, null, CancellationToken.None))
            .Single(entry => entry.Category == "job.completed" && entry.RelatedJobId == job.Id);
        Assert.Equal("Every file Deluno holds has been read and evaluated under its current preference plan. (Movies)", line.Message);
    }

    [Fact]
    public async Task The_switch_is_read_at_most_every_fifteen_seconds_but_the_liveness_heartbeat_is_written_whatever_it_says()
    {
        await using var rig = await StartAsync();
        var lane = Lane("import.files");
        await rig.SwitchAsync(true);
        await rig.EnqueueAsync("filesystem.import.execute");
        Assert.Single((await rig.TickAsync(lane)).Jobs);
        var first = await rig.HeartbeatAsync();
        Assert.False(string.IsNullOrEmpty(first));

        // Paused, but within fifteen seconds the lane still goes by what it read: it leases, and it does not beat again.
        await rig.SwitchAsync(false);
        rig.Clock.Advance(TimeSpan.FromSeconds(14));
        await rig.EnqueueAsync("filesystem.import.execute");
        Assert.Single((await rig.TickAsync(lane)).Jobs);
        Assert.Equal(first, await rig.HeartbeatAsync());

        // Past fifteen seconds the pause is felt: nothing is leased, yet the heartbeat is written all the same.
        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        var waiting = await rig.EnqueueAsync("filesystem.import.execute");
        Assert.Empty((await rig.TickAsync(lane)).Jobs);
        Assert.Equal("queued", (await rig.App.Services.GetRequiredService<IJobQueueRepository>().GetAsync(waiting.Id, CancellationToken.None))!.Status);
        Assert.NotEqual(first, await rig.HeartbeatAsync());
    }
}
