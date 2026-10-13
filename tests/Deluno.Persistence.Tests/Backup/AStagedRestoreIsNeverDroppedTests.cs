using Deluno.Contracts;
using Deluno.Infrastructure.Storage;

namespace Deluno.Persistence.Tests.Backup;

/// <summary>
/// #1260: the marker naming a staged restore was read with no handling. A cut-short marker named a folder that does not exist,
/// so the marker was deleted and the restore silently dropped.
/// </summary>
public sealed class AStagedRestoreIsNeverDroppedTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deluno-staged-" + Guid.NewGuid().ToString("N"));
    private string Marker => Path.Combine(_root, StagedRestore.StagingFolderName, "pending-restore.txt");
    private string Staged => Path.Combine(_root, StagedRestore.StagingFolderName, "20261014-090000");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void Stage(string markerText)
    {
        Directory.CreateDirectory(Staged);
        File.WriteAllText(Path.Combine(Staged, "platform.db"), "restored");
        File.WriteAllText(Marker, markerText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_marker_keeps_the_marker_and_the_staged_folder(string text)
    {
        Stage(text);

        var applied = StagedRestore.ApplyPending(_root);

        Assert.Empty(applied);
        Assert.True(File.Exists(Marker));
        Assert.True(File.Exists(Path.Combine(Staged, "platform.db")));
    }

    [Fact]
    public void A_cut_short_marker_keeps_the_marker_and_the_staged_folder_instead_of_dropping_the_restore()
    {
        Stage(Staged[..^6]);

        var applied = StagedRestore.ApplyPending(_root);

        Assert.Empty(applied);
        Assert.True(File.Exists(Marker));
        Assert.True(File.Exists(Path.Combine(Staged, "platform.db")));
    }

    [Fact]
    public void A_cut_short_marker_says_so_in_plain_words()
    {
        Stage(Staged[..^6]);
        var lines = new List<string>();

        StagedRestore.ApplyPending(_root, lines.Add);

        var line = Assert.Single(lines);
        Assert.Contains("could not be understood", line);
        Assert.Contains("nothing was removed", line);
    }

    [Fact]
    public void A_busy_marker_is_waited_for_and_never_throws_at_start()
    {
        Stage(Staged);
        var io = new Deluno.Tests.Support.ScriptedStateFileSystem { ReadFault = (_, _) => Deluno.Tests.Support.ScriptedStateFileSystem.SharingViolation() };
        var lines = new List<string>();

        var applied = StagedRestore.ApplyPending(_root, lines.Add, io);

        Assert.Empty(applied);
        Assert.True(File.Exists(Marker));
        Assert.True(File.Exists(Path.Combine(Staged, "platform.db")));
        Assert.Equal(StateFile.ReadAttempts - 1, io.Sleeps.Count);
        Assert.Contains("in use by something else", Assert.Single(lines));
    }

    [Fact]
    public void A_marker_held_by_another_handle_is_busy_not_a_crash()
    {
        Stage(Staged);
        using var other = new FileStream(Marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var io = new Deluno.Tests.Support.ScriptedStateFileSystem();

        Assert.Empty(StagedRestore.ApplyPending(_root, null, io));
        Assert.True(Directory.Exists(Staged));
    }

    [Fact]
    public void A_marker_that_is_busy_for_a_moment_is_read_and_the_restore_applied()
    {
        Stage(Staged);
        var io = new Deluno.Tests.Support.ScriptedStateFileSystem { ReadFault = (_, attempt) => attempt <= 4 ? Deluno.Tests.Support.ScriptedStateFileSystem.AccessDenied() : null };

        var applied = StagedRestore.ApplyPending(_root, null, io);

        Assert.Equal(["platform.db"], applied);
        Assert.Equal("restored", File.ReadAllText(Path.Combine(_root, "platform.db")));
        Assert.False(File.Exists(Marker));
    }

    [Fact]
    public void A_marker_whose_folder_is_gone_with_nothing_else_staged_is_still_cleared()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Marker)!);
        File.WriteAllText(Marker, Staged);

        Assert.Empty(StagedRestore.ApplyPending(_root));

        Assert.False(File.Exists(Marker));
    }

    [Fact]
    public void The_marker_is_written_atomically_and_arming_that_cannot_land_says_so()
    {
        Directory.CreateDirectory(Staged);
        StagedRestore.Arm(_root, Staged);
        Assert.Equal(Staged, File.ReadAllText(Marker));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Marker)!, "*.tmp"));

        var io = new Deluno.Tests.Support.ScriptedStateFileSystem { MoveFault = (_, _) => Deluno.Tests.Support.ScriptedStateFileSystem.SharingViolation() };
        Assert.Throws<StateFileException>(() => StagedRestore.Arm(_root, Staged, io));
    }
}
