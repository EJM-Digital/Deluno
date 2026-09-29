using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>#1080: a tray-hosted Deluno whose web host stopped by itself is started again, and says so if it cannot be.</summary>
public sealed class HostRestartSupervisorTests
{
    private sealed class Recorder
    {
        public List<string> Events { get; } = [];
        public List<TimeSpan> Pauses { get; } = [];

        public HostRestartReporter Reporter => new()
        {
            Restarting = (attempt, max, pause) => Events.Add($"restarting {attempt}/{max}"),
            Recovered = () => Events.Add("recovered"),
            AttemptFailed = (attempt, _) => Events.Add($"failed {attempt}"),
            GaveUp = max => Events.Add($"gave up {max}")
        };

        public Task Delay(TimeSpan span, CancellationToken token)
        {
            Pauses.Add(span);
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_stopped_host_is_started_again_after_a_pause()
    {
        var recorder = new Recorder();
        var starts = 0;
        var supervisor = new HostRestartSupervisor(_ => { starts++; return Task.CompletedTask; }, recorder.Reporter, recorder.Delay);

        await supervisor.HandleUnexpectedStopAsync();

        Assert.Equal(1, starts);
        Assert.Equal(["restarting 1/5", "recovered"], recorder.Events);
        Assert.Equal([TimeSpan.FromSeconds(15)], recorder.Pauses);
    }

    [Fact]
    public async Task A_host_that_will_not_start_is_tried_with_growing_pauses_up_to_the_cap_then_reported()
    {
        var recorder = new Recorder();
        var starts = 0;
        var supervisor = new HostRestartSupervisor(
            _ => { starts++; throw new InvalidOperationException("port in use"); },
            recorder.Reporter,
            recorder.Delay)
        { MaxAttempts = 3, BaseBackoff = TimeSpan.FromSeconds(10), MaxBackoff = TimeSpan.FromSeconds(30) };

        await supervisor.HandleUnexpectedStopAsync();

        Assert.Equal(3, starts);
        Assert.Equal([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30)], recorder.Pauses);
        Assert.Equal("gave up 3", recorder.Events[^1]);
        Assert.DoesNotContain("recovered", recorder.Events);
        Assert.Contains("choose Restart", HostRestartReporter.GaveUpMessage(3), StringComparison.OrdinalIgnoreCase);

        // Once it has given up it does not keep trying on its own.
        await supervisor.HandleUnexpectedStopAsync();
        Assert.Equal(3, starts);
    }

    [Fact]
    public async Task A_host_that_stays_up_for_a_while_starts_the_count_over()
    {
        var recorder = new Recorder();
        var clock = DateTimeOffset.UtcNow;
        var fail = true;
        var supervisor = new HostRestartSupervisor(
            _ => fail ? throw new InvalidOperationException("no") : Task.CompletedTask,
            recorder.Reporter,
            recorder.Delay,
            () => clock)
        { MaxAttempts = 2 };

        await supervisor.HandleUnexpectedStopAsync();
        Assert.Equal("gave up 2", recorder.Events[^1]);

        fail = false;
        supervisor.NoteStarted();
        clock += TimeSpan.FromMinutes(30);
        recorder.Events.Clear();
        await supervisor.HandleUnexpectedStopAsync();

        Assert.Equal(["restarting 1/2", "recovered"], recorder.Events);
    }

    [Fact]
    public async Task Choosing_Stop_or_Restart_by_hand_ends_a_restart_in_progress()
    {
        var recorder = new Recorder();
        var starts = 0;
        HostRestartSupervisor? supervisor = null;
        supervisor = new HostRestartSupervisor(
            _ => { starts++; return Task.CompletedTask; },
            recorder.Reporter,
            (span, token) => { supervisor!.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; });

        await supervisor.HandleUnexpectedStopAsync();

        Assert.Equal(0, starts);
        Assert.Equal(["restarting 1/5"], recorder.Events);
    }
}
