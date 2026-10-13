using Deluno.Infrastructure.Storage;
using Deluno.Tray;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// #1256: the start marker under a busy disk. A replace of the marker can make an open of it fail (a sharing violation, or
/// access denied while a delete is pending), and a scanner or a slow disk can hold the rename for a long stretch. A read that
/// meets that is "busy", never "unreadable"; a write that meets it keeps trying for a while and then says so once; and none of
/// it may stop the in-memory progress the supervisor judges a start by. No test here races a clock: every file call is injected.
/// </summary>
public sealed class StartMarkerBusyTests : IDisposable
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);
    private const string Path1 = @"C:\logs\start-progress.json";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "deluno-marker-" + Guid.NewGuid().ToString("N"));

    public StartMarkerBusyTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* temp */ }
    }

    private sealed class FakeIo : IMarkerFileSystem
    {
        public Dictionary<string, string> Files { get; } = [];
        public Func<int, Exception?>? OnRead { get; set; }
        public Func<int, Exception?>? OnMove { get; set; }
        public Func<int, Exception?>? OnWrite { get; set; }
        public List<TimeSpan> Sleeps { get; } = [];
        public int Reads;
        public int Moves;
        public int Writes;

        public TimeSpan Slept => TimeSpan.FromTicks(Sleeps.Sum(s => s.Ticks));
        public bool Exists(string path) => Files.ContainsKey(path);

        public string ReadAllText(string path)
        {
            if (OnRead?.Invoke(++Reads) is { } failure)
            {
                throw failure;
            }

            return Files[path];
        }

        public void WriteAllText(string path, string text)
        {
            if (OnWrite?.Invoke(++Writes) is { } failure)
            {
                throw failure;
            }

            Files[path] = text;
        }

        public void Move(string from, string to)
        {
            if (OnMove?.Invoke(++Moves) is { } failure)
            {
                throw failure;
            }

            Files[to] = Files[from];
            Files.Remove(from);
        }

        public void CreateDirectory(string path)
        {
        }

        public void Sleep(TimeSpan span) => Sleeps.Add(span);
    }

    private static string Valid(int pid = 100, string phase = "migrations")
        => $"{{\"pid\":{pid},\"attempt\":1,\"phase\":\"{phase}\",\"detail\":\"platform\",\"atUtc\":\"2026-10-13T09:00:00+00:00\"}}";

    // ------------------------------------------------------------------ the reader

    [Theory]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(IOException))]
    public void A_read_that_is_denied_or_blocked_while_the_marker_is_replaced_is_busy_then_retried(Type failure)
    {
        var io = new FakeIo { OnRead = call => call <= 3 ? (Exception)Activator.CreateInstance(failure, "held during a replace")! : null };
        io.Files[Path1] = Valid();

        var read = StartMarkerFile.Read(Path1, 100, io);

        Assert.Equal(StartMarkerKind.Own, read.Kind);
        Assert.Equal(3, io.Sleeps.Count);
        Assert.Equal(4, io.Reads);
    }

    [Fact]
    public void A_marker_that_stays_busy_for_the_whole_budget_is_busy_never_unreadable()
    {
        var io = new FakeIo { OnRead = _ => new UnauthorizedAccessException("a delete is pending") };
        io.Files[Path1] = Valid();

        var read = StartMarkerFile.Read(Path1, 100, io);

        Assert.Equal(StartMarkerKind.Busy, read.Kind);
        Assert.Null(read.Marker);
        // About a quarter of a second of waiting, and no more.
        Assert.InRange(io.Slept, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(300));
    }

    [Theory]
    [InlineData("{\"pid\":100,\"attempt\":1,\"pha")]
    [InlineData("")]
    [InlineData("not json at all")]
    public void A_file_whose_contents_cannot_be_parsed_is_still_unreadable_and_is_not_waited_for(string content)
    {
        var io = new FakeIo();
        io.Files[Path1] = content;

        var read = StartMarkerFile.Read(Path1, 100, io);

        Assert.Equal(StartMarkerKind.Unreadable, read.Kind);
        Assert.Empty(io.Sleeps);
        Assert.Equal(1, io.Reads);
    }

    [Fact]
    public void A_missing_file_is_missing_and_a_file_that_vanishes_between_the_check_and_the_read_too()
    {
        var io = new FakeIo();
        Assert.Equal(StartMarkerKind.Missing, StartMarkerFile.Read(Path1, 100, io).Kind);

        io.Files[Path1] = Valid();
        io.OnRead = _ => new FileNotFoundException();
        Assert.Equal(StartMarkerKind.Missing, StartMarkerFile.Read(Path1, 100, io).Kind);
        Assert.Empty(io.Sleeps);
    }

    /// <summary>The real file system, but the first time the reader waits, the other side lets go.</summary>
    private sealed class ReleasingIo(IDisposable holder) : IMarkerFileSystem
    {
        public int Sleeps;
        public bool Exists(string path) => File.Exists(path);
        public string ReadAllText(string path) => File.ReadAllText(path);
        public void WriteAllText(string path, string text) => File.WriteAllText(path, text);
        public void Move(string from, string to) => File.Move(from, to, overwrite: true);
        public void CreateDirectory(string path) => Directory.CreateDirectory(path);

        public void Sleep(TimeSpan span)
        {
            Sleeps++;
            holder.Dispose();
        }
    }

    [Fact]
    public void A_reader_meeting_a_file_held_open_the_way_a_replace_holds_it_waits_and_then_reads_it()
    {
        var path = Path.Combine(_folder, StartMarkerFile.FileName);
        File.WriteAllText(path, Valid());
        // Held with no sharing at all: opening it fails with a sharing violation, as it does while it is being replaced.
        var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var io = new ReleasingIo(held);

        var read = StartMarkerFile.Read(path, 100, io);

        Assert.Equal(StartMarkerKind.Own, read.Kind);
        Assert.Equal(1, io.Sleeps);
    }

    // ------------------------------------------------------------------ the writer

    [Theory]
    [InlineData(typeof(IOException), 12)]
    [InlineData(typeof(UnauthorizedAccessException), 12)]
    [InlineData(typeof(IOException), 30)]
    public void A_rename_that_fails_for_longer_than_the_old_five_tries_is_kept_trying_and_lands(Type failure, int times)
    {
        var io = new FakeIo { OnMove = call => call <= times ? (Exception)Activator.CreateInstance(failure, "a scanner has it")! : null };
        var marker = new StartMarker(100, 1, "migrations", "platform: V0003", Epoch);

        var problem = StartMarkerFile.Write(Path1, marker, io);

        Assert.Null(problem);
        Assert.Equal(times, io.Sleeps.Count);
        Assert.Equal(StartMarkerKind.Own, StartMarkerFile.Read(Path1, 100, io).Kind);
        Assert.Equal("platform: V0003", StartMarkerFile.Read(Path1, 100, io).Marker!.Detail);
    }

    [Fact]
    public void A_write_whose_temporary_file_is_held_is_tried_again_too()
    {
        var io = new FakeIo { OnWrite = call => call <= 4 ? new UnauthorizedAccessException("scanner") : null };

        Assert.Null(StartMarkerFile.Write(Path1, new StartMarker(100, 1, "ready", null, Epoch), io));
        Assert.Equal(StartMarkerKind.Own, StartMarkerFile.Read(Path1, 100, io).Kind);
    }

    [Fact]
    public void A_write_that_never_lands_gives_up_after_a_bounded_budget_and_says_why()
    {
        var io = new FakeIo { OnMove = _ => new IOException("the disk is busy") };

        var problem = StartMarkerFile.Write(Path1, new StartMarker(100, 1, "ready", null, Epoch), io);

        Assert.NotNull(problem);
        Assert.Contains("the disk is busy", problem);
        Assert.InRange(io.Slept, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void A_quick_write_makes_one_try_and_does_not_wait()
    {
        var io = new FakeIo { OnMove = _ => new IOException("busy") };

        var problem = StartMarkerFile.Write(Path1, new StartMarker(100, 1, "ready", null, Epoch), io, quick: true);

        Assert.NotNull(problem);
        Assert.Empty(io.Sleeps);
        Assert.Equal(1, io.Moves);
    }

    // ------------------------------------------------------------------ the progress does not depend on the file

    [Fact]
    public void A_marker_that_cannot_be_written_still_advances_the_in_memory_progress_and_is_said_once()
    {
        var time = new FakeTimeProvider(Epoch);
        var io = new FakeIo { OnMove = _ => new IOException("the disk is busy") };
        var log = new List<string>();
        var progress = new StartProgress(time, Path1, 100, log.Add, io);

        time.Advance(TimeSpan.FromMinutes(1));
        progress.Enter(StartupPhase.Migrations, "platform: V0001");
        time.Advance(TimeSpan.FromMinutes(1));
        progress.Enter(StartupPhase.Migrations, "platform: V0002");
        time.Advance(TimeSpan.FromMinutes(1));
        progress.Enter(StartupPhase.Migrations, "platform: V0003");

        Assert.Equal("platform: V0003", progress.Current.Detail);
        Assert.Equal(Epoch + TimeSpan.FromMinutes(3), progress.Current.AtUtc);
        Assert.Single(log, line => line.Contains("could not be written", StringComparison.OrdinalIgnoreCase));
        // Only the first write spent the budget; the later ones tried once each and did not wait.
        var firstBudget = io.Slept;
        Assert.InRange(firstBudget, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));

        // When it lands again that is said once too.
        io.OnMove = null;
        progress.Enter(StartupPhase.WebHost, "the databases are ready");
        Assert.Single(log, line => line.Contains("written again", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(StartMarkerKind.Own, StartMarkerFile.Read(Path1, 100, io).Kind);
    }

    [Fact]
    public async Task A_start_that_keeps_reporting_steps_is_never_judged_hung_because_its_marker_cannot_be_written()
    {
        var time = new FakeTimeProvider(Epoch);
        var io = new FakeIo { OnMove = _ => new UnauthorizedAccessException("the marker is held for ever") };
        var progress = new StartProgress(time, Path1, 100, null, io);
        var windows = new StartWindows();
        progress.Enter(StartupPhase.Migrations, "platform: V0000");
        var watching = StartHangWatch.WaitForHangAsync(progress, windows, time, CancellationToken.None);

        // 40 minutes of steps, one every five: far past the 15 minute window, and not one marker lands.
        for (var step = 1; step <= 8; step++)
        {
            AdvanceInline(time, TimeSpan.FromMinutes(5));
            progress.Enter(StartupPhase.Migrations, $"platform: V{step:0000}");
            await Task.Yield();
        }

        Assert.False(watching.IsCompleted, "a healthy start with an unwritable marker was called hung");
        Assert.Equal(StartMarkerKind.Missing, StartMarkerFile.Read(Path1, 100, io).Kind);
        progress.Enter(StartupPhase.Ready);
        Assert.Null(await watching.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private static void AdvanceInline(FakeTimeProvider time, TimeSpan span)
    {
        var prior = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try { time.Advance(span); } finally { SynchronizationContext.SetSynchronizationContext(prior); }
    }

    private sealed class StuckMoveIo(ManualResetEventSlim entered, ManualResetEventSlim release) : IMarkerFileSystem
    {
        public bool Exists(string path) => false;
        public string ReadAllText(string path) => throw new FileNotFoundException();
        public void WriteAllText(string path, string text) { }
        public void CreateDirectory(string path) { }
        public void Sleep(TimeSpan span) { }

        public void Move(string from, string to)
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        }
    }

    [Fact]
    public async Task What_the_supervisor_reads_is_not_held_up_by_a_marker_write_that_is_stuck()
    {
        var time = new FakeTimeProvider(Epoch);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var progress = new StartProgress(time, Path1, 100, null, new StuckMoveIo(entered, release));

        var stepping = Task.Run(() => progress.Enter(StartupPhase.OpeningDatabases, "games"));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the write never began");

        // The write is stuck inside the rename. The snapshot the supervisor watches is already there and can be read.
        var read = await Task.Run(() => progress.Current).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(StartupPhase.OpeningDatabases, read.Phase);
        Assert.Equal("games", read.Detail);

        release.Set();
        await stepping.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
