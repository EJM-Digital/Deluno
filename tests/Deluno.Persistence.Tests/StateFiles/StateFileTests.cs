using System.Text.Json;
using Deluno.Contracts;
using Deluno.Tests.Support;

namespace Deluno.Persistence.Tests.StateFiles;

/// <summary>
/// The one shared reader and writer for Deluno's own JSON state files (#1260). Every pause goes through a recorded fake, so
/// "waits about a quarter of a second" is a count of pauses, not a wait.
/// </summary>
public sealed class StateFileTests : IDisposable
{
    private sealed record Sample(string Name, int Count);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "deluno-statefile-" + Guid.NewGuid().ToString("N"));
    private readonly ScriptedStateFileSystem _io = new();
    private string PathOf => Path.Combine(_root, "state.json");

    public StateFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void A_missing_file_reads_as_missing_and_is_the_only_case_that_may_start_empty()
    {
        var read = StateFile.Read<Sample>(PathOf, io: _io);

        Assert.Equal(StateFileKind.Missing, read.Kind);
        Assert.True(read.CanBuildOn);
    }

    [Fact]
    public void A_file_that_is_there_reads_as_ok()
    {
        File.WriteAllText(PathOf, JsonSerializer.Serialize(new Sample("a", 2)));

        var read = StateFile.Read<Sample>(PathOf, io: _io);

        Assert.Equal(StateFileKind.Ok, read.Kind);
        Assert.Equal(new Sample("a", 2), read.Value);
    }

    [Theory]
    [InlineData("{\"Name\":\"a\",\"Cou")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("not json at all")]
    public void A_cut_short_empty_or_foreign_file_reads_as_unreadable_never_as_empty(string text)
    {
        File.WriteAllText(PathOf, text);

        var read = StateFile.Read<Sample>(PathOf, io: _io);

        Assert.Equal(StateFileKind.Unreadable, read.Kind);
        Assert.False(read.CanBuildOn);
        Assert.Null(read.Value);
    }

    [Fact]
    public void A_file_held_by_another_handle_with_no_sharing_reads_as_busy_after_a_bounded_wait()
    {
        File.WriteAllText(PathOf, "{\"Name\":\"a\",\"Count\":1}");
        using var other = new FileStream(PathOf, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var read = StateFile.Read<Sample>(PathOf, io: _io);

        Assert.Equal(StateFileKind.Busy, read.Kind);
        Assert.False(read.CanBuildOn);
        // 26 looks, 25 pauses of 10 ms: about a quarter of a second, and none of it real.
        Assert.Equal(StateFile.ReadAttempts - 1, _io.Sleeps.Count);
        Assert.All(_io.Sleeps, pause => Assert.Equal(StateFile.ReadPause, pause));
    }

    [Fact]
    public void A_file_with_a_delete_pending_reads_as_busy_because_access_is_denied_until_it_goes()
    {
        File.WriteAllText(PathOf, "{\"Name\":\"a\",\"Count\":1}");
        _io.ReadFault = (_, _) => ScriptedStateFileSystem.AccessDenied();

        var read = StateFile.Read<Sample>(PathOf, io: _io);

        Assert.Equal(StateFileKind.Busy, read.Kind);
    }

    [Fact]
    public void A_busy_file_is_read_once_it_clears_inside_the_wait()
    {
        File.WriteAllText(PathOf, JsonSerializer.Serialize(new Sample("a", 7)));
        _io.ReadFault = (_, attempt) => attempt <= 5 ? ScriptedStateFileSystem.SharingViolation() : null;

        var read = StateFile.Read<Sample>(PathOf, io: _io);

        Assert.Equal(StateFileKind.Ok, read.Kind);
        Assert.Equal(7, read.Value!.Count);
        Assert.Equal(5, _io.Sleeps.Count);
    }

    [Fact]
    public void A_file_removed_while_it_is_being_read_reads_as_missing()
    {
        File.WriteAllText(PathOf, "{}");
        _io.ReadFault = (_, _) => new FileNotFoundException();

        Assert.Equal(StateFileKind.Missing, StateFile.Read<Sample>(PathOf, io: _io).Kind);
    }

    [Fact]
    public void A_write_lands_through_a_temporary_file_and_a_rename_and_leaves_none_behind()
    {
        var problem = StateFile.Write(PathOf, "{\"Name\":\"b\",\"Count\":2}", _io);

        Assert.Null(problem);
        Assert.Equal("{\"Name\":\"b\",\"Count\":2}", File.ReadAllText(PathOf));
        Assert.Equal([PathOf], Directory.GetFiles(_root));
        Assert.Equal(1, _io.Moves);
    }

    [Fact]
    public void A_write_whose_rename_is_busy_is_retried_and_lands_when_it_clears()
    {
        File.WriteAllText(PathOf, "old");
        _io.MoveFault = (_, attempt) => attempt <= 3 ? ScriptedStateFileSystem.SharingViolation() : null;

        var problem = StateFile.Write(PathOf, "new", _io);

        Assert.Null(problem);
        Assert.Equal("new", File.ReadAllText(PathOf));
        Assert.Equal(3, _io.Sleeps.Count);
        Assert.All(_io.Sleeps, pause => Assert.Equal(StateFile.WritePause, pause));
    }

    [Fact]
    public void A_write_that_never_lands_reports_why_leaves_the_old_file_whole_and_removes_its_temporary_file()
    {
        File.WriteAllText(PathOf, "old");
        _io.MoveFault = (_, _) => ScriptedStateFileSystem.AccessDenied();

        var problem = StateFile.Write(PathOf, "new", _io);

        Assert.NotNull(problem);
        Assert.Equal("old", File.ReadAllText(PathOf));
        Assert.Equal([PathOf], Directory.GetFiles(_root));
        Assert.Equal(StateFile.WriteAttempts - 1, _io.Sleeps.Count);
    }

    [Fact]
    public void An_update_changes_what_is_there_under_the_lock()
    {
        File.WriteAllText(PathOf, JsonSerializer.Serialize(new Sample("a", 1)));

        var result = StateFile.Update<Sample>(PathOf, current => current! with { Count = current.Count + 1 }, "The sample", io: _io);

        Assert.True(result.Landed);
        Assert.Equal(2, StateFile.Read<Sample>(PathOf, io: _io).Value!.Count);
    }

    [Fact]
    public void An_update_of_a_missing_file_starts_from_nothing()
    {
        var result = StateFile.Update<Sample>(PathOf, current => current ?? new Sample("first", 1), "The sample", io: _io);

        Assert.True(result.Landed);
        Assert.Equal("first", StateFile.Read<Sample>(PathOf, io: _io).Value!.Name);
    }

    [Fact]
    public void An_update_that_returns_nothing_deletes_the_file()
    {
        File.WriteAllText(PathOf, JsonSerializer.Serialize(new Sample("a", 1)));

        var result = StateFile.Update<Sample>(PathOf, _ => null, "The sample", io: _io);

        Assert.True(result.Landed);
        Assert.False(File.Exists(PathOf));
    }

    [Fact]
    public void An_update_of_a_busy_file_changes_nothing_and_says_so_without_calling_the_change()
    {
        File.WriteAllText(PathOf, JsonSerializer.Serialize(new Sample("a", 1)));
        _io.ReadFault = (_, _) => ScriptedStateFileSystem.SharingViolation();
        var called = false;

        var result = StateFile.Update<Sample>(PathOf, current => { called = true; return current; }, "The sample", io: _io);

        Assert.False(result.Landed);
        Assert.False(called);
        Assert.Contains("The sample", result.Problem);
        Assert.Contains("in use", result.Problem);
        Assert.Equal(new Sample("a", 1), JsonSerializer.Deserialize<Sample>(File.ReadAllText(PathOf)));
    }

    [Fact]
    public void An_update_whose_lock_another_process_holds_changes_nothing_after_a_bounded_wait()
    {
        File.WriteAllText(PathOf, JsonSerializer.Serialize(new Sample("a", 1)));
        _io.LockHeldByAnother = true;

        var result = StateFile.Update<Sample>(PathOf, current => current! with { Count = 99 }, "The sample", io: _io);

        Assert.False(result.Landed);
        Assert.Equal(1, JsonSerializer.Deserialize<Sample>(File.ReadAllText(PathOf))!.Count);
        Assert.Equal(StateFile.LockAttempts - 1, _io.Sleeps.Count);
    }

    [Fact]
    public void An_update_holds_off_while_a_real_second_handle_holds_the_lock_file()
    {
        File.WriteAllText(PathOf, JsonSerializer.Serialize(new Sample("a", 1)));
        // What another process (the tray, or the server in service mode) does: it holds the lock file open with no sharing.
        using var otherProcess = new FileStream(StateFile.LockPathFor(PathOf), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var result = StateFile.Update<Sample>(PathOf, current => current! with { Count = 99 }, "The sample", io: _io);

        Assert.False(result.Landed);
        Assert.Equal(1, JsonSerializer.Deserialize<Sample>(File.ReadAllText(PathOf))!.Count);
    }

    [Fact]
    public void An_update_of_a_cut_short_file_writes_nothing_and_keeps_one_timestamped_copy_however_often_it_is_asked()
    {
        const string cut = "{\"Name\":\"a\",\"Cou";
        File.WriteAllText(PathOf, cut);
        var called = false;

        var first = StateFile.Update<Sample>(PathOf, current => { called = true; return current; }, "The sample", io: _io);
        var second = StateFile.Update<Sample>(PathOf, current => { called = true; return current; }, "The sample", io: _io);

        Assert.False(first.Landed);
        Assert.False(second.Landed);
        Assert.False(called);
        Assert.Equal(cut, File.ReadAllText(PathOf));
        var copies = Directory.GetFiles(_root, "state.json.unreadable-*");
        Assert.Single(copies);
        Assert.Equal("state.json.unreadable-20261014-093000", Path.GetFileName(copies[0]));
        Assert.Equal(cut, File.ReadAllText(copies[0]));
        Assert.Contains("damaged", first.Problem);
        Assert.Contains("state.json.unreadable-20261014-093000", first.Problem);
    }

    [Fact]
    public void An_update_may_start_fresh_from_a_cut_short_file_only_when_the_caller_says_so_and_a_copy_is_kept()
    {
        File.WriteAllText(PathOf, "{\"Na");

        var result = StateFile.Update<Sample>(PathOf, current => current ?? new Sample("fresh", 1), "The sample", io: _io, whenUnreadable: UnreadableBehavior.StartFresh);

        Assert.True(result.Landed);
        Assert.Equal("fresh", StateFile.Read<Sample>(PathOf, io: _io).Value!.Name);
        Assert.Single(Directory.GetFiles(_root, "state.json.unreadable-*"));
    }

    [Fact]
    public void An_update_that_cannot_write_reports_it_instead_of_pretending()
    {
        File.WriteAllText(PathOf, JsonSerializer.Serialize(new Sample("a", 1)));
        _io.MoveFault = (_, _) => ScriptedStateFileSystem.SharingViolation();

        var result = StateFile.Update<Sample>(PathOf, current => current! with { Count = 5 }, "The sample", io: _io);

        Assert.False(result.Landed);
        Assert.Contains("could not be saved", result.Problem);
        Assert.Equal(1, JsonSerializer.Deserialize<Sample>(File.ReadAllText(PathOf))!.Count);
    }

    [Fact]
    public void UpdateOrThrow_throws_a_state_file_exception_that_is_an_io_exception_with_the_plain_reason()
    {
        File.WriteAllText(PathOf, "{\"Na");

        var exception = Assert.Throws<StateFileException>(() => StateFile.UpdateOrThrow<Sample>(PathOf, c => c, "The sample", io: _io));

        Assert.IsAssignableFrom<IOException>(exception);
        Assert.Contains("damaged", exception.Message);
    }

    [Fact]
    public async Task Two_threads_changing_the_same_file_through_independent_instances_lose_nothing()
    {
        File.WriteAllText(PathOf, JsonSerializer.Serialize(new Sample("count", 0)));
        const int each = 40;

        void Work()
        {
            // Its own file system object, as a second process would have: nothing is shared but the disk.
            var io = new RealStateFileSystem();
            for (var i = 0; i < each; i++)
            {
                var result = StateFile.Update<Sample>(PathOf, current => current! with { Count = current.Count + 1 }, "The sample", io: io);
                Assert.True(result.Landed, result.Problem);
            }
        }

        var a = Task.Run(Work);
        var b = Task.Run(Work);
        await Task.WhenAll(a, b);

        Assert.Equal(each * 2, StateFile.Read<Sample>(PathOf).Value!.Count);
    }

    [Fact]
    public void A_locked_read_is_busy_while_another_holds_the_lock_and_never_empty()
    {
        File.WriteAllText(PathOf, JsonSerializer.Serialize(new Sample("a", 1)));
        _io.LockHeldByAnother = true;

        Assert.Equal(StateFileKind.Busy, StateFile.ReadLocked<Sample>(PathOf, io: _io).Kind);
    }

    [Fact]
    public void A_delete_waits_out_a_busy_file_and_treats_a_missing_one_as_gone()
    {
        File.WriteAllText(PathOf, "x");
        _io.DeleteFault = (_, attempt) => attempt <= 2 ? ScriptedStateFileSystem.AccessDenied() : null;

        Assert.Null(StateFile.Delete(PathOf, _io));
        Assert.False(File.Exists(PathOf));
        Assert.Null(StateFile.Delete(PathOf, _io));
    }
}
