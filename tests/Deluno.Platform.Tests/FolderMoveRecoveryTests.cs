namespace Deluno.Platform.Tests;

public sealed class FolderMoveRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deluno-move-recovery-" + Guid.NewGuid().ToString("N"));

    public FolderMoveRecoveryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void A_folder_that_was_moved_goes_back_to_where_it_came_from()
    {
        var source = Path.Combine(_root, "from");
        var destination = Path.Combine(_root, "to");
        Directory.CreateDirectory(destination);

        Assert.True(FolderMoveRecovery.TryPutBack(source, destination));
        Assert.True(Directory.Exists(source));
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public void A_folder_that_is_not_at_the_new_place_cannot_be_put_back()
    {
        var source = Path.Combine(_root, "from");

        Assert.False(FolderMoveRecovery.TryPutBack(source, Path.Combine(_root, "missing")));
        Assert.False(Directory.Exists(source));
    }

    [Fact]
    public void A_folder_the_system_will_not_let_go_of_cannot_be_put_back()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return; // reason: only a non-root Unix user is refused by a read-only parent
        }

        var parent = Path.Combine(_root, "locked");
        var destination = Path.Combine(parent, "to");
        Directory.CreateDirectory(destination);
        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.False(FolderMoveRecovery.TryPutBack(Path.Combine(_root, "from"), destination));
        }
        finally
        {
            File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void A_source_that_is_not_a_path_is_a_mistake_not_a_failed_move()
        => Assert.Throws<ArgumentException>(() => FolderMoveRecovery.TryPutBack("", _root));

    [Fact]
    public void A_folder_that_was_put_back_says_nothing_changed()
    {
        var message = FolderMoveRecovery.Describe("Nosferatu", "/a", "/b", putBack: true, "locked");

        Assert.Equal("Nosferatu: Deluno could not update its records for the move, so the folder was put back and nothing changed.", message);
    }

    [Theory]
    [InlineData(null, "Move it back to /a.")]
    [InlineData("", "Move it back to /a.")]
    [InlineData("run Rescan folder", "Move it back to /a, or run Rescan folder.")]
    public void A_folder_left_at_the_new_place_says_where_it_is_and_how_to_fix_it(string? whereToFix, string ending)
    {
        var message = FolderMoveRecovery.Describe("Nosferatu", "/a", "/b", putBack: false, "database is locked", whereToFix);

        Assert.StartsWith("Nosferatu: the folder is now at /b, but Deluno could not update its records (database is locked). ", message);
        Assert.EndsWith(ending, message);
    }

    [Fact]
    public void A_refused_move_asks_for_permission_to_be_checked()
    {
        var message = FolderMoveRecovery.DescribeDiskFailure("Nosferatu", "/a", "/b", new UnauthorizedAccessException());

        Assert.Equal("Nosferatu: Deluno is not allowed to move the folder /a. Check its permissions and try again.", message);
    }

    [Fact]
    public void A_move_to_another_drive_names_both_drives()
    {
        var message = FolderMoveRecovery.DescribeDiskFailure("Nosferatu", "C:/a", "C:/", "D:/b", "d:/", new IOException("x"));

        Assert.Contains("on another drive (C:/) from the new library (d:/)", message);
        Assert.Contains("Rescan folder", message);
    }

    [Fact]
    public void A_drive_letter_in_another_case_is_still_the_same_drive()
    {
        var message = FolderMoveRecovery.DescribeDiskFailure("Nosferatu", "C:/a", "C:/", "c:/b", "c:/", new IOException("disk full"));

        Assert.Equal("Nosferatu: the folder could not be moved (disk full), so nothing was changed.", message);
    }

    [Fact]
    public void Folders_found_by_path_are_compared_by_their_real_drives()
    {
        var message = FolderMoveRecovery.DescribeDiskFailure("Nosferatu", Path.Combine(_root, "a"), Path.Combine(_root, "b"), new IOException("in use"));

        Assert.Equal("Nosferatu: the folder could not be moved (in use), so nothing was changed.", message);
    }
}
