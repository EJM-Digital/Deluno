using System.Diagnostics;
using Deluno.Infrastructure.Storage;
using Deluno.Tray;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// #1256: the previous release in the golden-path update scenario (rc.30.25) leaves no running mark, so the install hook
/// asks the one thing every release does while it runs: the job worker's heartbeat, written to jobs.db (in WAL mode, so to
/// jobs.db-wal) every 15 seconds. DelunoHeartbeatWorker and SqliteJobStore.HeartbeatAsync are the same in rc.30.20 and now.
/// </summary>
public sealed class HeartbeatEvidenceTests : IDisposable
{
    private const string Exe = @"C:\Users\Example\AppData\Local\Deluno\current\Deluno.exe";
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "deluno-heartbeat-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(Now);
    private readonly List<ProcessStartInfo> _started = [];
    private readonly List<string> _log = [];

    public HeartbeatEvidenceTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_folder, recursive: true); } catch { /* temp */ }
    }

    private static Func<string, DateTimeOffset?> Writes(params (string File, DateTimeOffset At)[] files) =>
        path => files.Where(f => Path.GetFileName(path) == f.File).Select(f => (DateTimeOffset?)f.At).FirstOrDefault();

    private TimeSpan? Age(Func<string, DateTimeOffset?> lastWrite) => HeartbeatEvidence.Age(["r"], _time, lastWrite);

    // The evidence: what a heartbeat does to the files, and what a hard kill leaves.

    [Fact]
    public void A_heartbeat_commit_in_the_mode_Deluno_uses_appends_to_the_jobs_wal_file_and_moves_its_write_time()
    {
        var database = Path.Combine(_folder, "jobs.db");
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        Exec(connection, "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA wal_autocheckpoint = 2000;");
        Exec(connection, "CREATE TABLE worker_heartbeats (worker_id TEXT PRIMARY KEY, last_seen_utc TEXT NOT NULL);");

        var before = WriteTime(database + "-wal");
        var lengthBefore = new FileInfo(database + "-wal").Length;
        Thread.Sleep(300); // reason: the file system's own modified time must tick over before the write whose time is compared
        // The statement SqliteJobStore.HeartbeatAsync runs.
        Exec(connection, "INSERT INTO worker_heartbeats (worker_id, last_seen_utc) VALUES ('worker-a', '2026-10-13T09:00:15Z') ON CONFLICT(worker_id) DO UPDATE SET last_seen_utc = excluded.last_seen_utc;");

        Assert.True(File.Exists(database + "-wal"));
        Assert.True(new FileInfo(database + "-wal").Length >= lengthBefore);
        Assert.True(WriteTime(database + "-wal") > before, "a heartbeat must move the write time of jobs.db-wal while the connection is open");
        var age = HeartbeatEvidence.Age([_folder], TimeProvider.System, HeartbeatEvidence.LastWriteOnDisk);
        Assert.True(age < TimeSpan.FromSeconds(10), $"age was {age}");
    }

    [Fact]
    public void A_process_killed_hard_leaves_the_time_of_its_last_write_on_the_file()
    {
        if (!OperatingSystem.IsWindows()) { return; }

        // The child holds the file open, appends to it the way SQLite writes the WAL, and is then ended with
        // TerminateProcess, which is how Velopack ends Deluno.
        var path = Path.Combine(_folder, "jobs.db-wal");
        var script = $"$f = [IO.File]::Open('{path}', 'Append', 'Write', 'ReadWrite'); " +
                     "$b = [Text.Encoding]::ASCII.GetBytes('heartbeat'); " +
                     "while ($true) { $f.Write($b, 0, $b.Length); $f.Flush(); Start-Sleep -Milliseconds 200 }";
        using var child = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", script },
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        Assert.True(SpinWait.SpinUntil(() => File.Exists(path) && new FileInfo(path).Length > 0, 30000), "the child did not write");
        Thread.Sleep(1500); // reason: the child must keep the file open and writing for a real while so the time taken while it is open is measured
        var whileOpen = WriteTime(path);

        child.Kill(entireProcessTree: true);
        Assert.True(child.WaitForExit(10000));
        var killedAt = DateTime.UtcNow;
        var afterKill = WriteTime(path);

        Assert.True(afterKill >= whileOpen, "the time went backwards after the kill");
        Assert.True(killedAt - afterKill < TimeSpan.FromSeconds(5), $"the last write ({afterKill:O}) is not the time of the last write before the kill ({killedAt:O})");
        Thread.Sleep(1000); // reason: shows no further write after the kill; absence needs real time to wrongly write
        Assert.Equal(afterKill, WriteTime(path));
        var age = HeartbeatEvidence.Age([_folder], TimeProvider.System, HeartbeatEvidence.LastWriteOnDisk);
        Assert.True(age < HeartbeatEvidence.Window, $"age after the kill was {age}");
    }

    [Fact]
    public void The_database_the_heartbeat_writes_to_is_the_one_the_evidence_reads()
        => Assert.Equal("jobs.db", DelunoDatabaseNames.Jobs + ".db");

    [Fact]
    public void The_evidence_reads_the_database_and_its_wal()
        => Assert.Equal(["jobs.db", "jobs.db-wal"], HeartbeatEvidence.FileNames);

    // The decision.

    [Fact]
    public void A_fresh_wal_write_is_recent_and_a_stale_one_is_not()
    {
        Assert.True(HeartbeatEvidence.IsRecent(Age(Writes(("jobs.db-wal", Now - TimeSpan.FromSeconds(12))))));
        Assert.False(HeartbeatEvidence.IsRecent(Age(Writes(("jobs.db-wal", Now - TimeSpan.FromMinutes(10))))));
    }

    [Fact]
    public void The_window_edges_are_inclusive_at_90_seconds_and_not_a_moment_beyond()
    {
        Assert.Equal(TimeSpan.FromSeconds(90), HeartbeatEvidence.Window);
        Assert.True(HeartbeatEvidence.IsRecent(Age(Writes(("jobs.db", Now - TimeSpan.FromSeconds(90))))));
        Assert.False(HeartbeatEvidence.IsRecent(Age(Writes(("jobs.db", Now - TimeSpan.FromSeconds(90) - TimeSpan.FromMilliseconds(1))))));
        Assert.True(HeartbeatEvidence.IsRecent(Age(Writes(("jobs.db", Now)))));
    }

    [Fact]
    public void The_window_follows_the_clock_not_the_file()
    {
        var file = Writes(("jobs.db", Now));
        Assert.True(HeartbeatEvidence.IsRecent(Age(file)));
        _time.Advance(TimeSpan.FromSeconds(91));
        Assert.False(HeartbeatEvidence.IsRecent(Age(file)));
    }

    [Fact]
    public void The_newer_of_the_database_and_its_wal_counts()
        => Assert.Equal(TimeSpan.FromSeconds(20), Age(Writes(("jobs.db", Now - TimeSpan.FromHours(3)), ("jobs.db-wal", Now - TimeSpan.FromSeconds(20)))));

    [Fact]
    public void Missing_files_are_no_evidence_and_a_clock_that_moved_back_counts_as_now()
    {
        Assert.Null(Age(_ => null));
        Assert.False(HeartbeatEvidence.IsRecent(null));
        Assert.Equal(TimeSpan.Zero, Age(Writes(("jobs.db", Now + TimeSpan.FromMinutes(5)))));
    }

    [Fact]
    public void Every_folder_Deluno_may_keep_its_data_in_is_looked_in_the_settings_one_first()
    {
        var state = new AppSettingsPathState(@"C:\u\DelunoConfig\deluno.json", @"C:\p\Deluno\data\deluno.json", false, false, @"C:\u\DelunoData", @"C:\p\Deluno\data");

        Assert.Equal([@"D:\Media\DelunoData", @"C:\u\DelunoData", @"C:\p\Deluno\data"], HeartbeatEvidence.DataRoots(@"D:\Media\DelunoData", state));
        Assert.Equal(2, HeartbeatEvidence.DataRoots(@"c:\u\delunodata", state).Count());
        Assert.Equal(2, HeartbeatEvidence.DataRoots(null, state).Count());
    }

    [Fact]
    public void A_data_root_from_the_settings_is_read_from_the_real_files()
    {
        var custom = Path.Combine(_folder, "custom");
        Directory.CreateDirectory(custom);
        File.WriteAllText(Path.Combine(custom, "jobs.db-wal"), "x");
        var state = new AppSettingsPathState(Path.Combine(_folder, "c", "d.json"), Path.Combine(_folder, "l", "d.json"), false, false, Path.Combine(_folder, "none"), Path.Combine(_folder, "none2"));

        var age = HeartbeatEvidence.Age(HeartbeatEvidence.DataRoots(custom, state), TimeProvider.System, HeartbeatEvidence.LastWriteOnDisk);

        Assert.True(HeartbeatEvidence.IsRecent(age));
    }

    // The hook's use of it.

    private sealed class Store : IRunningMarkStore
    {
        public RunningMark? Mark { get; set; }
        public RunningMark? Read(RunningKind kind) => kind == RunningKind.Tray ? Mark : null;
        public void Write(RunningKind kind, RunningMark mark) => Mark = mark;
        public void Remove(RunningKind kind) => Mark = null;
    }

    private RelaunchKind Schedule(Store store, TimeSpan? heartbeatAge, bool serviceInstalled = false)
        => RelaunchAfterSetup.Schedule(new RunningMarks(store), Exe, _ => false, () => serviceInstalled, () => 7624, () => heartbeatAge, "x.log", _started.Add, _log.Add);

    private static Store WithMark() => new() { Mark = new RunningMark(1, 2, Exe) };

    [Fact]
    public void A_mark_alone_starts_Deluno()
        => Assert.Equal(RelaunchKind.Tray, Schedule(WithMark(), heartbeatAge: null));

    [Fact]
    public void A_heartbeat_alone_starts_Deluno_and_the_log_says_how_it_was_known_and_what_a_false_positive_means()
    {
        Assert.Equal(RelaunchKind.Tray, Schedule(new Store(), TimeSpan.FromSeconds(14)));

        Assert.Single(_started);
        Assert.Contains(_log, l => l.Contains("No record that Deluno was running") && l.Contains("14 seconds") && l.Contains("started anyway"));
    }

    [Fact]
    public void A_heartbeat_alone_in_service_mode_restarts_the_service()
        => Assert.Equal(RelaunchKind.Service, Schedule(new Store(), TimeSpan.FromSeconds(5), serviceInstalled: true));

    [Fact]
    public void Neither_a_mark_nor_a_recent_heartbeat_leaves_Deluno_stopped()
    {
        Assert.Equal(RelaunchKind.None, Schedule(new Store(), TimeSpan.FromMinutes(20)));
        Assert.Equal(RelaunchKind.None, Schedule(new Store(), null));
        Assert.Empty(_started);
    }

    [Fact]
    public void A_mark_and_a_heartbeat_together_start_Deluno_once()
    {
        Schedule(WithMark(), TimeSpan.FromSeconds(3));
        Assert.Single(_started);
    }

    private static DateTime WriteTime(string path) => new FileInfo(path).LastWriteTimeUtc;

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
