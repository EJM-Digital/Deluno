using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Worker.Dashboard;
using Deluno.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Worker.Tests.Dashboard;

/// <summary>The pieces the Dashboard summary is made of (#1082, #1081), each on its own.</summary>
public sealed class DashboardBuildingBlocksTests
{
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public async Task Many_callers_at_once_share_one_build_and_a_fresh_value_is_not_rebuilt()
    {
        var clock = new ManualClock();
        var cache = new AsyncValueCache<string>(TimeSpan.FromSeconds(4), clock);
        var builds = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<string> Build(CancellationToken _)
        {
            Interlocked.Increment(ref builds);
            await release.Task;
            return "built";
        }

        var callers = Enumerable.Range(0, 25).Select(_ => cache.GetAsync(Build, CancellationToken.None)).ToArray();
        release.SetResult();
        var results = await Task.WhenAll(callers);

        Assert.All(results, result => Assert.Equal("built", result));
        Assert.Equal(1, builds);

        clock.Advance(TimeSpan.FromSeconds(3));
        await cache.GetAsync(Build, CancellationToken.None);
        Assert.Equal(1, builds);

        clock.Advance(TimeSpan.FromSeconds(2));
        await cache.GetAsync(Build, CancellationToken.None);
        Assert.Equal(2, builds);
    }

    [Fact]
    public async Task A_cache_that_serves_stale_answers_at_once_and_rebuilds_in_the_background()
    {
        var clock = new ManualClock();
        var cache = new AsyncValueCache<string>(TimeSpan.FromSeconds(10), clock, serveStale: true);
        var version = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<string> Build(CancellationToken _)
        {
            var mine = Interlocked.Increment(ref version);
            if (mine > 1)
            {
                await gate.Task;
            }

            return $"v{mine}";
        }

        Assert.Equal("v1", await cache.GetAsync(Build, CancellationToken.None));

        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal("v1", await cache.GetAsync(Build, CancellationToken.None));

        gate.SetResult();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (cache.Peek() != "v2" && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10); // reason: poll interval inside a bounded wait (3 s above)
        }

        Assert.Equal("v2", cache.Peek());
    }

    [Fact]
    public async Task A_failed_build_reaches_the_caller_and_the_next_caller_tries_again()
    {
        var cache = new AsyncValueCache<string>(TimeSpan.FromSeconds(4), new ManualClock());
        var attempts = 0;

        Task<string> Build(CancellationToken _)
            => ++attempts == 1 ? throw new InvalidOperationException("no") : Task.FromResult("ok");

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(Build, CancellationToken.None));
        Assert.Equal("ok", await cache.GetAsync(Build, CancellationToken.None));
    }

    [Fact]
    public async Task The_lane_board_says_running_idle_failing_restarting_and_disabled()
    {
        var board = new JobLaneStateBoard(TimeProvider.System);
        var supervisor = new LaneSupervisor(NullLogger.Instance, null, TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(10), board);

        string State(string lane) => board.Snapshot().Single(item => item.Name == lane).State;

        board.Started("import");
        Assert.Equal(JobLaneStateBoard.Idle, State("import"));

        board.JobStarted("import");
        Assert.Equal(JobLaneStateBoard.Running, State("import"));
        Assert.Equal(1, board.Snapshot().Single().ActiveJobs);
        board.JobFinished("import");
        board.JobFinished("import");
        Assert.Equal(JobLaneStateBoard.Idle, State("import"));

        supervisor.OnFailureStreak("import", LaneSupervisor.FailureStreakThreshold, new InvalidOperationException("database is locked"));
        Assert.Equal(JobLaneStateBoard.Failing, State("import"));
        Assert.Equal("database is locked", board.Snapshot().Single().LastError);
        supervisor.OnFailureStreak("import", 0, null);
        Assert.Equal(JobLaneStateBoard.Idle, State("import"));

        board.MarkDisabled("search");
        Assert.Equal(JobLaneStateBoard.Disabled, State("search"));

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        var task = supervisor.SuperviseAsync("metadata", async token =>
        {
            if (Interlocked.Increment(ref runs) == 1)
            {
                throw new InvalidOperationException("lane crashed");
            }

            restarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, token); // reason: a build that never finishes until the test releases or cancels it
        }, stop.Token);
        await restarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        // The lane is running again, so it is no longer shown as restarting.
        Assert.Equal(JobLaneStateBoard.Failing, State("metadata"));
        stop.Cancel();
        await task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void The_pause_gate_reads_a_queue_it_already_holds_per_media_type()
    {
        var overview = Overview(
            Item("a", "movies", DownloadQueueStatuses.Downloading),
            Item("b", "movies", DownloadQueueStatuses.Queued),
            Item("c", "movies", DownloadQueueStatuses.Queued),
            Item("d", "tv", DownloadQueueStatuses.Downloading),
            Item("e", "movies", DownloadQueueStatuses.Imported));

        var movies = SearchQueuePauseGate.Decide(2, "movies", overview);
        var tv = SearchQueuePauseGate.Decide(2, "tv", overview);

        Assert.True(movies.Paused);
        Assert.Equal(3, movies.Waiting);
        Assert.False(tv.Paused);
        Assert.Equal(1, tv.Waiting);
        Assert.False(movies.Unreadable);
    }

    [Fact]
    public void The_pause_gate_says_unreadable_when_a_client_did_not_answer()
    {
        var healthy = Overview(Item("a", "movies", DownloadQueueStatuses.Downloading));
        var broken = healthy with
        {
            Clients =
            [
                healthy.Clients[0] with
                {
                    LastFailure = new IntegrationFailure(
                        "download-client", "c1", "Client", "queue",
                        IntegrationFailureKind.Timeout, IntegrationRetryState.Retrying, "no answer")
                }
            ]
        };

        var decision = SearchQueuePauseGate.Decide(1, "movies", broken);

        Assert.True(decision.Unreadable);
        Assert.False(decision.Paused);
        Assert.Contains("did not answer", decision.UnreadableReason);
    }

    [Fact]
    public void A_leftover_scan_is_unknown_until_one_has_run()
    {
        var memory = new LeftoverScanMemory();
        Assert.Null(memory.Last);

        var when = new DateTimeOffset(2026, 9, 30, 8, 0, 0, TimeSpan.Zero);
        memory.Record(3, 4096, when);

        Assert.Equal(new LeftoverScanReading(3, 4096, when), memory.Last);
    }

    [Theory]
    [InlineData("download-client", "/indexers/download-clients")]
    [InlineData("indexer", "/indexers/indexers")]
    [InlineData("library", "/settings/libraries")]
    [InlineData("processor", "/settings/processing")]
    [InlineData("system", "/system")]
    [InlineData("something-new", "/system")]
    public void A_problem_links_to_the_screen_that_clears_it(string category, string expected)
        => Assert.Equal(expected, DashboardSummaryService.LinkFor(category));

    private static DownloadQueueItem Item(string id, string mediaType, string status) => new(
        id, "c1", "Client", "torrent", mediaType, id, id, "cat", status, 10, 1, 0, 100, 10, 0, "Idx", null, DateTimeOffset.UtcNow);

    private static DownloadTelemetryOverview Overview(params DownloadQueueItem[] items)
    {
        var summary = new DownloadTelemetrySummary(items.Length, 0, 0, 0, 0, 0, 0);
        var client = new DownloadClientTelemetrySnapshot(
            "c1", "Client", "torrent", null, "healthy", null,
            new DownloadClientTelemetryCapabilities(true, true, true, true, true, true, "none"),
            summary, items, [], DateTimeOffset.UtcNow);
        return new DownloadTelemetryOverview(summary, [client], DateTimeOffset.UtcNow);
    }
}
