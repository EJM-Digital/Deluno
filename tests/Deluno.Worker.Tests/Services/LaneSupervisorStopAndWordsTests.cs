using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Worker.Services;
using Deluno.Worker.Tests.Support;
using Microsoft.Extensions.Logging;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// #1080: how a lane supervisor behaves when Deluno stops at any moment (before a run, during one, during the pause), what it
/// tells Health in words, how long it pauses between restarts, and what the lane board shows.
/// </summary>
public sealed class LaneSupervisorStopAndWordsTests
{
    private sealed class Reports : IBackgroundProblemReporter
    {
        public List<(string Key, string Title, string Message, string Reason)> Reported { get; } = [];
        public List<string> Cleared { get; } = [];

        public void Report(string key, string title, string message, string reason)
        {
            lock (Reported) { Reported.Add((key, title, message, reason)); }
        }

        public void Clear(string key)
        {
            lock (Cleared) { Cleared.Add(key); }
        }
    }

    [Fact]
    public async Task A_supervisor_told_to_stop_before_it_begins_never_starts_the_lane()
    {
        var supervisor = new LaneSupervisor(new RecordingLogger<LaneSupervisor>(), new Reports());
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();
        var runs = 0;

        await supervisor.SuperviseAsync("import", _ => { runs++; return Task.CompletedTask; }, stop.Token);

        Assert.Equal(0, runs);
    }

    [Fact]
    public async Task A_lane_that_fails_because_Deluno_is_stopping_is_not_reported_or_restarted()
    {
        var reports = new Reports();
        var supervisor = new LaneSupervisor(new RecordingLogger<LaneSupervisor>(), reports, TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5));
        using var stop = new CancellationTokenSource();
        var runs = 0;

        await supervisor.SuperviseAsync("import", async _ =>
        {
            runs++;
            await stop.CancelAsync();
            throw new IOException("the database closed as Deluno stopped");
        }, stop.Token);

        Assert.Equal(1, runs);
        Assert.Empty(reports.Reported);
    }

    [Fact]
    public async Task A_lane_cancelled_by_the_stop_ends_the_supervisor_quietly()
    {
        var reports = new Reports();
        var supervisor = new LaneSupervisor(new RecordingLogger<LaneSupervisor>(), reports);
        using var stop = new CancellationTokenSource();

        var task = supervisor.SuperviseAsync("import", token => Task.Delay(Timeout.Infinite, token), stop.Token);
        await stop.CancelAsync();
        await task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Empty(reports.Reported);
    }

    [Fact]
    public async Task Stopping_during_the_pause_before_a_restart_ends_the_supervisor_at_once()
    {
        var reports = new Reports();
        var supervisor = new LaneSupervisor(new RecordingLogger<LaneSupervisor>(), reports, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        using var stop = new CancellationTokenSource();
        var runs = 0;

        var task = supervisor.SuperviseAsync("import", _ => { runs++; throw new InvalidOperationException("boom"); }, stop.Token);
        await Wait.UntilAsync(() => reports.Reported.Count == 1, "the failure to be reported");
        await stop.CancelAsync();
        await task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task A_stopped_lane_tells_Health_what_happened_in_plain_words_and_counts_its_restarts()
    {
        var reports = new Reports();
        var board = new JobLaneStateBoard(TimeProvider.System);
        var supervisor = new LaneSupervisor(new RecordingLogger<LaneSupervisor>(), reports, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), board);
        using var stop = new CancellationTokenSource();
        var runs = 0;

        var task = supervisor.SuperviseAsync("import", _ =>
        {
            if (Interlocked.Increment(ref runs) == 1)
            {
                throw new InvalidOperationException("returned 3 jobs for 2 slots");
            }

            return Task.CompletedTask; // ends quietly the second time
        }, stop.Token);
        await Wait.UntilAsync(() => reports.Reported.Count >= 2, "two stops to be reported");
        await stop.CancelAsync();
        await task.WaitAsync(TimeSpan.FromSeconds(3));

        var first = reports.Reported[0];
        Assert.Equal("lane:import", first.Key);
        Assert.Equal("Background jobs: import lane stopped", first.Title);
        Assert.Equal("Deluno's import background jobs stopped after an error and are being restarted (restart 1). Work on this lane is delayed until it is back.", first.Message);
        Assert.Equal("returned 3 jobs for 2 slots", first.Reason);
        var second = reports.Reported[1];
        Assert.Contains("(restart 2)", second.Message);
        Assert.Equal("The import lane ended without being asked to stop.", second.Reason);
    }

    [Fact]
    public async Task The_pause_between_restarts_doubles_and_never_passes_the_longest_allowed()
    {
        var log = new RecordingLogger<LaneSupervisor>();
        var reports = new Reports();
        var supervisor = new LaneSupervisor(log, reports, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(25));
        using var stop = new CancellationTokenSource();

        var task = supervisor.SuperviseAsync("search", _ => throw new InvalidOperationException("boom"), stop.Token);
        await Wait.UntilAsync(() => reports.Reported.Count >= 4, "four stops to be reported");
        await stop.CancelAsync();
        await task.WaitAsync(TimeSpan.FromSeconds(3));

        var errors = log.Entries.Where(entry => entry.Level == LogLevel.Error).Take(4).Select(entry => entry.Message).ToArray();
        Assert.Equal("Job lane search stopped unexpectedly; restarting it in 00:00:00.0100000.", errors[0]);
        Assert.Equal("Job lane search stopped unexpectedly; restarting it in 00:00:00.0200000.", errors[1]);
        Assert.Equal("Job lane search stopped unexpectedly; restarting it in 00:00:00.0250000.", errors[2]);
        Assert.Equal("Job lane search stopped unexpectedly; restarting it in 00:00:00.0250000.", errors[3]);
        Assert.IsType<InvalidOperationException>(log.Entries.First(entry => entry.Level == LogLevel.Error).Exception);
    }

    [Fact]
    public async Task The_board_shows_a_lane_as_restarting_with_its_error_and_running_again_once_it_is_started()
    {
        var board = new JobLaneStateBoard(TimeProvider.System);
        var supervisor = new LaneSupervisor(new RecordingLogger<LaneSupervisor>(), null, TimeSpan.FromHours(1), TimeSpan.FromHours(1), board);
        using var stop = new CancellationTokenSource();

        var task = supervisor.SuperviseAsync("import", _ => throw new InvalidOperationException("boom"), stop.Token);
        await Wait.UntilAsync(() => board.Snapshot().Any(reading => reading.State == JobLaneStateBoard.Restarting), "the lane to be shown as restarting");
        var reading = Assert.Single(board.Snapshot());
        await stop.CancelAsync();
        await task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal("import", reading.Name);
        Assert.Equal("boom", reading.LastError);
    }

    [Fact]
    public void A_lane_failing_less_often_than_the_threshold_says_nothing()
    {
        var reports = new Reports();
        var board = new JobLaneStateBoard(TimeProvider.System);
        var supervisor = new LaneSupervisor(new RecordingLogger<LaneSupervisor>(), reports, board: board);

        supervisor.OnFailureStreak("import", 1, new IOException("busy"));
        supervisor.OnFailureStreak("import", 2, new IOException("busy"));

        Assert.Empty(reports.Reported);
        Assert.Empty(reports.Cleared);
        Assert.Null(Assert.Single(board.Snapshot()).LastError);
    }

    [Fact]
    public void A_lane_failing_three_times_in_a_row_is_reported_with_the_last_error_and_a_recovery_clears_it()
    {
        var reports = new Reports();
        var board = new JobLaneStateBoard(TimeProvider.System);
        var supervisor = new LaneSupervisor(new RecordingLogger<LaneSupervisor>(), reports, board: board);

        supervisor.OnFailureStreak("import", 3, new IOException("database is busy"));

        var report = Assert.Single(reports.Reported);
        Assert.Equal("lane:import", report.Key);
        Assert.Equal("Background jobs: import lane keeps failing", report.Title);
        Assert.Equal("Deluno's import background jobs have failed 3 times in a row and are waiting to try again. Work on this lane is delayed until it succeeds.", report.Message);
        Assert.Equal("database is busy", report.Reason);
        Assert.Equal(JobLaneStateBoard.Failing, Assert.Single(board.Snapshot()).State);

        supervisor.OnFailureStreak("import", 0, null);

        Assert.Equal(["lane:import"], reports.Cleared);
        Assert.Equal(JobLaneStateBoard.Idle, Assert.Single(board.Snapshot()).State);
    }

    [Fact]
    public void A_streak_with_no_error_to_show_is_not_reported_and_a_supervisor_without_a_reporter_still_keeps_the_board()
    {
        var reports = new Reports();
        var board = new JobLaneStateBoard(TimeProvider.System);
        new LaneSupervisor(new RecordingLogger<LaneSupervisor>(), reports, board: board).OnFailureStreak("import", 5, null);
        new LaneSupervisor(new RecordingLogger<LaneSupervisor>(), null, board: board).OnFailureStreak("search", 3, new IOException("busy"));

        Assert.Empty(reports.Reported);
        Assert.Equal("busy", board.Snapshot().Single(reading => reading.Name == "search").LastError);
    }

    [Fact]
    public void The_problem_key_names_the_lane()
    {
        Assert.Equal("lane:import", LaneSupervisor.ProblemKey("import"));
        Assert.Equal(3, LaneSupervisor.FailureStreakThreshold);
    }
}
