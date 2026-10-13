using Deluno.Api.Backup;
using Deluno.Infrastructure.Storage;
using Deluno.Tray;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>#1256: the start-up phase and progress record, and the windows measured from the real phase timings.</summary>
public sealed class StartProgressTests : IDisposable
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "deluno-progress-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(Epoch);
    private readonly List<string> _log = [];

    public StartProgressTests() => Directory.CreateDirectory(_folder);

    private string Marker => Path.Combine(_folder, StartMarkerFile.FileName);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* temp */ }
    }

    [Fact]
    public void Entering_a_phase_writes_the_marker_with_the_phase_the_step_and_the_time_before_the_step_runs()
    {
        var progress = new StartProgress(_time, Marker, pid: 100, _log.Add);

        _time.Advance(TimeSpan.FromSeconds(7));
        progress.Enter(StartupPhase.Migrations, "platform: V0012 AddThing");

        var read = StartMarkerFile.Read(Marker, ownPid: 100);
        Assert.Equal(StartMarkerKind.Own, read.Kind);
        Assert.Equal(100, read.Marker!.Pid);
        Assert.Equal("migrations", read.Marker.Phase);
        Assert.Equal("platform: V0012 AddThing", read.Marker.Detail);
        Assert.Equal(Epoch + TimeSpan.FromSeconds(7), read.Marker.AtUtc);
        Assert.Equal(StartupPhase.Migrations, progress.Current.Phase);
        Assert.Equal(Epoch + TimeSpan.FromSeconds(7), progress.Current.AtUtc);
    }

    [Fact]
    public void Starting_and_opening_the_databases_are_logged_before_anything_that_can_block()
    {
        var progress = new StartProgress(_time, Marker, pid: 100, _log.Add);

        progress.BeginAttempt(1);
        progress.Enter(StartupPhase.OpeningDatabases, "movies");

        Assert.Contains(_log, line => line.Contains("is starting") && line.Contains("100"));
        Assert.Contains(_log, line => line.Contains("opening the databases", StringComparison.OrdinalIgnoreCase) && line.Contains("movies"));
    }

    [Fact]
    public void Each_phase_from_start_to_ready_is_written_in_order()
    {
        var progress = new StartProgress(_time, Marker, pid: 100, _log.Add);
        var seen = new List<string>();
        foreach (var phase in new[] { StartupPhase.PreUpdateBackup, StartupPhase.OpeningDatabases, StartupPhase.Migrations, StartupPhase.WebHost, StartupPhase.Ready })
        {
            progress.Enter(phase);
            seen.Add(StartMarkerFile.Read(Marker, 100).Marker!.Phase);
        }

        Assert.Equal(["backup", "databases", "migrations", "web-host", "ready"], seen);
    }

    [Fact]
    public async Task The_marker_is_replaced_atomically_so_a_reader_never_sees_a_torn_file()
    {
        var progress = new StartProgress(_time, Marker, pid: 100);
        // A marker is there before the reader starts, so "missing" is never what it sees.
        progress.Enter(StartupPhase.Migrations, "first");
        using var stop = new CancellationTokenSource();
        var torn = 0;
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (StartMarkerFile.Read(Marker, 100).Kind == StartMarkerKind.Unreadable)
                {
                    Interlocked.Increment(ref torn);
                }
            }
        });

        for (var i = 0; i < 300; i++)
        {
            progress.Enter(StartupPhase.Migrations, "step " + i + new string('x', i % 50));
        }

        stop.Cancel();
        await reader;
        Assert.Equal(0, torn);
        Assert.False(File.Exists(Marker + ".tmp"), "the temporary file is renamed over the marker, never left behind");
    }

    [Fact]
    public void A_marker_from_another_process_is_reported_as_the_previous_run_and_never_counts_as_progress()
    {
        StartMarkerFile.Write(Marker, new StartMarker(999, 2, "migrations", "platform: V0009", Epoch - TimeSpan.FromHours(1)));

        var read = StartMarkerFile.Read(Marker, ownPid: 100);
        Assert.Equal(StartMarkerKind.OtherProcess, read.Kind);

        var progress = new StartProgress(_time, Marker, pid: 100, _log.Add);
        Assert.NotNull(progress.PreviousRun);
        Assert.Equal(999, progress.PreviousRun!.Pid);
        Assert.Equal("migrations", progress.PreviousRun.Phase);
        // This run's clock starts now, not an hour ago.
        Assert.Equal(Epoch, progress.Current.AtUtc);
        Assert.Equal(100, progress.Current.Pid);
        Assert.Contains(_log, line => line.Contains("previous", StringComparison.OrdinalIgnoreCase) && line.Contains("999") && line.Contains("updating the databases"));
    }

    [Fact]
    public void A_previous_run_that_got_to_ready_is_not_a_failed_start()
    {
        StartMarkerFile.Write(Marker, new StartMarker(999, 1, "ready", null, Epoch - TimeSpan.FromDays(1)));

        var progress = new StartProgress(_time, Marker, pid: 100, _log.Add);

        Assert.Null(progress.PreviousRun);
        Assert.DoesNotContain(_log, line => line.Contains("previous", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("{\"pid\":100,\"attempt\":1,\"pha")]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{\"pid\":100,\"attempt\":1,\"phase\":\"no-such-phase\",\"atUtc\":\"2026-10-13T09:00:00Z\"}")]
    public void A_half_written_or_unreadable_marker_is_ignored_and_the_next_step_replaces_it(string content)
    {
        File.WriteAllText(Marker, content);

        Assert.Equal(StartMarkerKind.Unreadable, StartMarkerFile.Read(Marker, 100).Kind);

        var progress = new StartProgress(_time, Marker, pid: 100, _log.Add);
        Assert.Null(progress.PreviousRun);
        progress.Enter(StartupPhase.OpeningDatabases, "games");
        Assert.Equal(StartMarkerKind.Own, StartMarkerFile.Read(Marker, 100).Kind);
    }

    [Fact]
    public void A_missing_marker_and_an_unwritable_folder_never_stop_a_start()
    {
        Assert.Equal(StartMarkerKind.Missing, StartMarkerFile.Read(Path.Combine(_folder, "nope", "x.json"), 100).Kind);

        var blocker = Path.Combine(_folder, "file-not-folder");
        File.WriteAllText(blocker, "x");
        var progress = new StartProgress(_time, Path.Combine(blocker, StartMarkerFile.FileName), pid: 100, _log.Add);

        progress.Enter(StartupPhase.Migrations, "platform");

        Assert.Equal(StartupPhase.Migrations, progress.Current.Phase);
    }

    [Fact]
    public void Beginning_an_attempt_goes_back_to_the_first_phase_with_a_fresh_time()
    {
        var progress = new StartProgress(_time, Marker, pid: 100, _log.Add);
        progress.Enter(StartupPhase.Migrations, "platform");
        _time.Advance(TimeSpan.FromMinutes(20));

        progress.BeginAttempt(2);

        Assert.Equal(StartupPhase.Starting, progress.Current.Phase);
        Assert.Equal(2, progress.Current.Attempt);
        Assert.Null(progress.Current.Detail);
        Assert.Equal(_time.GetUtcNow(), progress.Current.AtUtc);
        Assert.Equal("starting", StartMarkerFile.Read(Marker, 100).Marker!.Phase);
    }

    [Fact]
    public void Changes_are_announced_so_the_tray_can_say_what_it_is_doing()
    {
        var progress = new StartProgress(_time, Marker, pid: 100);
        var seen = new List<StartupPhase>();
        progress.Changed += snapshot => seen.Add(snapshot.Phase);

        progress.Enter(StartupPhase.PreUpdateBackup, "file 1 of 9");
        progress.Enter(StartupPhase.Ready);

        Assert.Equal([StartupPhase.PreUpdateBackup, StartupPhase.Ready], seen);
    }

    // ------------------------------------------------------------------ the windows, from the real timings

    [Fact]
    public void The_backup_window_is_longer_than_the_backup_s_own_time_limit()
    {
        // The backup cancels itself after PreUpdateBackupGate.BackupTimeout; the supervisor must not cut in before that.
        Assert.True(new StartWindows().PreUpdateBackup > PreUpdateBackupGate.BackupTimeout);
    }

    [Fact]
    public void The_database_window_outlasts_one_locked_database_being_waited_for()
    {
        // SqliteLockRetry waits up to MaxWait for one database; that wait is a plain log line, not a hang.
        Assert.True(new StartWindows().OpeningDatabases > new MigrationLockRetryOptions().MaxWait);
        Assert.True(new StartWindows().Migrations > new MigrationLockRetryOptions().MaxWait);
    }

    [Fact]
    public void Every_phase_but_ready_has_a_window_and_ready_has_none()
    {
        var windows = new StartWindows();
        foreach (var phase in Enum.GetValues<StartupPhase>().Where(p => p != StartupPhase.Ready))
        {
            Assert.True(windows.For(phase) > TimeSpan.Zero && windows.For(phase) < TimeSpan.FromHours(1), phase.ToString());
        }

        Assert.Equal(Timeout.InfiniteTimeSpan, windows.For(StartupPhase.Ready));
    }

    [Fact]
    public void The_phase_words_round_trip_through_their_keys()
    {
        foreach (var phase in Enum.GetValues<StartupPhase>())
        {
            Assert.True(StartupPhases.TryParse(StartupPhases.Key(phase), out var back));
            Assert.Equal(phase, back);
            Assert.False(string.IsNullOrWhiteSpace(StartupPhases.Words(phase)));
        }

        Assert.False(StartupPhases.TryParse("nope", out _));
    }
}
