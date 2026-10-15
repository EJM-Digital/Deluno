using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Worker.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// #1261: a processor that comes back was shown healthy only after 60 s plus a whole maintenance run (never, with
/// automatic jobs off). The watcher checks on its own fixed schedule: 15 s while a processor is down, 30 s while it
/// is healthy, every processor in parallel, never tied to the automatic-jobs switch. All on a fake clock.
/// </summary>
public sealed class ProcessorHealthWatcherTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan TargetBack = TimeSpan.FromSeconds(20);   // 15 s cadence + 2 s tick + slack
    private static readonly TimeSpan TargetDown = TimeSpan.FromSeconds(45);   // 30 s cadence + 2 s tick + 12 s hang

    public enum Reply { Answered, Unreachable, Refused, Hang }

    private sealed class Rig
    {
        public readonly FakeTimeProvider Clock = new(Start);
        public readonly Dictionary<string, ProcessorConnectionItem> Items = new();
        public readonly Dictionary<string, Reply> Replies = new();
        public readonly Dictionary<string, int> Probes = new();
        public readonly Dictionary<string, TimeSpan> Latency = new();
        public readonly List<Task> Outstanding = [];
        public ProcessorHealthWatcher Watcher = null!;
        public int SettingsReads;
        private int _ticks;
        private int _parked;

        /// <summary>
        /// A probe is only ever unfinished because it is waiting on the fake clock or a cancel (parked), or because
        /// it was just woken and the thread pool has not yet run its continuation. A fixed number of yields does
        /// not cover the second case on a machine with one or two cores (CI's): the next tick then skips the
        /// processor as "still being checked" and the stored status lands a whole real-time scheduling delay late.
        /// So each step waits, in real time and bounded, until every unfinished probe is parked.
        /// </summary>
        private async Task SettleAsync()
        {
            var deadline = Environment.TickCount64 + 10_000;
            while (Outstanding.Count(task => !task.IsCompleted) > Volatile.Read(ref _parked) && Environment.TickCount64 < deadline)
            {
                await Task.Delay(1);
            }

            for (var y = 0; y < 30; y++) await Task.Yield();
        }

        public void Add(string id, string health = "healthy", Reply reply = Reply.Answered, bool enabled = true)
        {
            Items[id] = new ProcessorConnectionItem(id, id, "generic", "http://processor.invalid/intake", "Authorization", null, enabled, health, null, null, Clock.GetUtcNow(), Clock.GetUtcNow());
            Replies[id] = reply;
            Probes[id] = 0;
        }

        public Rig Build()
        {
            var repo = new Mock<IProcessorRepository>();
            repo.Setup(r => r.ListProcessorConnectionsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Items.Values.ToArray());
            repo.Setup(r => r.RecordProcessorConnectionHealthAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string id, string status, string? message, CancellationToken _) =>
                {
                    Items[id] = Items[id] with { HealthStatus = status, LastHealthMessage = message };
                    return Items[id];
                });
            var service = new Mock<IProcessorConnectionService>();
            service.Setup(s => s.GetStatusSupportAsync(It.IsAny<ProcessorConnectionItem>(), It.IsAny<CancellationToken>()))
                .Returns(async (ProcessorConnectionItem item, CancellationToken token) =>
                {
                    Probes[item.Id]++;
                    if (Latency.TryGetValue(item.Id, out var latency))
                    {
                        var wait = new TaskCompletionSource();
                        using var timer = Clock.CreateTimer(_ => wait.TrySetResult(), null, latency, Timeout.InfiniteTimeSpan);
                        using (token.Register(() => wait.TrySetCanceled(token)))
                        {
                            Interlocked.Increment(ref _parked);
                            try { await wait.Task; } finally { Interlocked.Decrement(ref _parked); }
                        }
                    }

                    switch (Replies[item.Id])
                    {
                        case Reply.Hang:
                            // Cancelled inline, so the fake clock decides exactly when the hang is cut off.
                            var never = new TaskCompletionSource<ProcessorStatusSupport>();
                            using (token.Register(() => never.TrySetCanceled(token)))
                            {
                                Interlocked.Increment(ref _parked);
                                try { return await never.Task; } finally { Interlocked.Decrement(ref _parked); }
                            }
                        case Reply.Unreachable:
                            return new ProcessorStatusSupport(ProcessorAnswer.Unreachable, false, false, $"Deluno could not reach {item.Name}.");
                        case Reply.Refused:
                            return new ProcessorStatusSupport(ProcessorAnswer.Forbidden, false, false, $"{item.Name} refused the key.");
                        default:
                            return new ProcessorStatusSupport(ProcessorAnswer.Answered, true, true, null);
                    }
                });
            var settings = new Mock<IPlatformSettingsRepository>();
            settings.Setup(s => s.GetAsync(It.IsAny<CancellationToken>()))
                .Callback(() => SettingsReads++)
                .ThrowsAsync(new InvalidOperationException("health must not read the automatic-jobs switch"));
            var services = new ServiceCollection()
                .AddSingleton(repo.Object).AddSingleton(service.Object).AddSingleton(settings.Object)
                .BuildServiceProvider();
            Watcher = new ProcessorHealthWatcher(NullLogger<ProcessorHealthWatcher>.Instance, services.GetRequiredService<IServiceScopeFactory>(), Clock);
            return this;
        }

        /// <summary>Moves the clock a second at a time and, every TickInterval, runs one tick, as the hosted loop would.</summary>
        public async Task StepAsync(int seconds = 1)
        {
            for (var i = 0; i < seconds; i++)
            {
                Clock.Advance(TimeSpan.FromSeconds(1));
                if (++_ticks % (int)ProcessorHealthWatcher.TickInterval.TotalSeconds == 0)
                {
                    Outstanding.AddRange(await Watcher.TickAsync(CancellationToken.None));
                }

                await SettleAsync();
            }
        }

        public async Task<TimeSpan?> UntilAsync(Func<bool> condition, TimeSpan limit)
        {
            var start = Clock.GetUtcNow();
            while (Clock.GetUtcNow() - start <= limit)
            {
                if (condition()) return Clock.GetUtcNow() - start;
                await StepAsync();
            }

            return condition() ? Clock.GetUtcNow() - start : null;
        }
    }

    [Theory]
    [InlineData(Reply.Unreachable, "unreachable")]
    [InlineData(Reply.Refused, "degraded")]
    public async Task A_down_processor_that_answers_again_is_healthy_within_the_target_whatever_point_of_the_cycle_it_returns(Reply was, string stored)
    {
        for (var offset = 0; offset < 31; offset++)
        {
            var rig = new Rig();
            rig.Add("p1", "healthy", was);
            rig.Build();
            await rig.UntilAsync(() => rig.Items["p1"].HealthStatus == stored, TimeSpan.FromSeconds(60));
            Assert.Equal(stored, rig.Items["p1"].HealthStatus);

            await rig.StepAsync(offset);
            rig.Replies["p1"] = Reply.Answered;
            var took = await rig.UntilAsync(() => rig.Items["p1"].HealthStatus == "healthy", TimeSpan.FromMinutes(2));

            Assert.True(took is not null && took <= TargetBack, $"offset {offset}s: back to healthy took {took}");
        }
    }

    [Theory]
    [InlineData(Reply.Unreachable, "unreachable")]
    [InlineData(Reply.Refused, "degraded")]
    [InlineData(Reply.Hang, "unreachable")]
    public async Task A_healthy_processor_that_stops_is_stored_down_within_the_target_whatever_point_of_the_cycle_it_stops(Reply now, string stored)
    {
        for (var offset = 0; offset < 31; offset++)
        {
            var rig = new Rig();
            rig.Add("p1");
            rig.Build();
            await rig.StepAsync(offset + 3);
            rig.Replies["p1"] = now;
            var took = await rig.UntilAsync(() => rig.Items["p1"].HealthStatus == stored, TimeSpan.FromMinutes(2));

            Assert.True(took is not null && took <= TargetDown, $"offset {offset}s: down took {took}; probes {rig.Probes["p1"]}; status {rig.Items["p1"].HealthStatus}");
        }
    }

    [Fact]
    public async Task A_down_processor_is_checked_on_a_fixed_schedule_not_a_while_after_the_last_check_ended()
    {
        var rig = new Rig();
        rig.Add("p1", "unreachable", Reply.Unreachable);
        rig.Latency["p1"] = TimeSpan.FromSeconds(9);
        rig.Build();

        await rig.StepAsync(150);

        // 150 s / 15 s = 10 starts. "15 s after the last one ended" would give 6.
        Assert.InRange(rig.Probes["p1"], 9, 11);
    }

    [Fact]
    public async Task A_healthy_processor_is_checked_about_every_thirty_seconds()
    {
        var rig = new Rig();
        rig.Add("p1");
        rig.Build();

        await rig.StepAsync(180);

        Assert.InRange(rig.Probes["p1"], 5, 7);
    }

    [Fact]
    public async Task A_hanging_processor_never_holds_up_another_and_is_not_probed_twice_at_once()
    {
        var rig = new Rig();
        rig.Add("hung", "healthy", Reply.Hang);
        rig.Add("back", "unreachable", Reply.Unreachable);
        rig.Add("steady");
        rig.Build();
        await rig.StepAsync(10);

        rig.Replies["back"] = Reply.Answered;
        var took = await rig.UntilAsync(() => rig.Items["back"].HealthStatus == "healthy", TimeSpan.FromMinutes(1));

        Assert.True(took is not null && took <= TargetBack, $"back took {took} beside a hanging processor");
        // The hanging one is cut off at the probe timeout and shown down, and only one probe is ever out for it.
        await rig.UntilAsync(() => rig.Items["hung"].HealthStatus == "unreachable", TimeSpan.FromSeconds(60));
        Assert.Equal("unreachable", rig.Items["hung"].HealthStatus);
        var elapsed = (rig.Clock.GetUtcNow() - Start).TotalSeconds;
        Assert.True(rig.Probes["hung"] <= 1 + elapsed / ProcessorHealthWatcher.ProbeTimeout.TotalSeconds);
        Assert.Equal("healthy", rig.Items["steady"].HealthStatus);
    }

    [Fact]
    public async Task Health_never_reads_the_automatic_jobs_switch_so_it_runs_with_automatic_jobs_off()
    {
        var rig = new Rig();
        rig.Add("p1", "unreachable", Reply.Answered);
        rig.Build();

        var took = await rig.UntilAsync(() => rig.Items["p1"].HealthStatus == "healthy", TimeSpan.FromSeconds(30));

        Assert.NotNull(took);
        Assert.Equal(0, rig.SettingsReads);
    }

    [Fact]
    public async Task A_disabled_processor_is_never_asked_and_a_new_one_is_asked_at_once()
    {
        var rig = new Rig();
        rig.Add("off", "healthy", Reply.Unreachable, enabled: false);
        rig.Build();
        await rig.StepAsync(60);
        Assert.Equal(0, rig.Probes["off"]);

        rig.Add("fresh", "healthy", Reply.Unreachable);
        var took = await rig.UntilAsync(() => rig.Items["fresh"].HealthStatus == "unreachable", TimeSpan.FromSeconds(10));
        Assert.NotNull(took);
    }

    [Fact]
    public async Task A_processor_marked_down_by_a_hand_off_is_looked_at_again_within_the_down_cadence()
    {
        var rig = new Rig();
        rig.Add("p1");
        rig.Build();
        await rig.StepAsync(31);      // just probed; the next healthy look is 30 s away
        rig.Items["p1"] = rig.Items["p1"] with { HealthStatus = "unreachable" };   // written by the hand-off watcher
        rig.Replies["p1"] = Reply.Answered;

        var took = await rig.UntilAsync(() => rig.Items["p1"].HealthStatus == "healthy", TimeSpan.FromSeconds(40));

        Assert.True(took is not null && took <= TargetBack, $"took {took}");
    }

    [Fact]
    public void The_old_maintenance_pass_no_longer_probes_processors_so_there_is_one_owner()
    {
        Assert.Null(typeof(WorkPlanner).GetMethod("RunProcessorHealthPassAsync"));
    }
}
