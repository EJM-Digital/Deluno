using Deluno.Infrastructure.Storage;

namespace Deluno.Persistence.Tests.Backup;

/// <summary>
/// Restoring a backup onto a running Deluno.
///
/// <para>Found on #81 by taking a backup on an installed build, deleting a
/// library, and restoring. It returned a 500 and changed nothing. The only
/// trace was a single <c>cache.db.pre-restore</c> copy: the restore had written
/// its "keep the old one" copy for the first database and then thrown trying to
/// overwrite the live file. Deluno holds every database open for as long as it
/// runs, so on Windows the very first extract fails.</para>
///
/// <para>The staging folder was already being created and never written to,
/// which is the shape of an intention nobody finished. It is finished now:
/// the upload lands in staging, and it is applied at startup before anything
/// opens a database.</para>
/// </summary>
public sealed class StagedRestoreTests
{
    [Fact]
    public void A_staged_restore_replaces_the_file_and_keeps_the_one_it_replaced()
    {
        var root = Directory.CreateTempSubdirectory("deluno-restore").FullName;
        try
        {
            var live = Path.Combine(root, "platform.db");
            File.WriteAllText(live, "the database as it is now");

            var staged = Path.Combine(root, StagedRestore.StagingFolderName, "20260904-000000");
            Directory.CreateDirectory(staged);
            File.WriteAllText(Path.Combine(staged, "platform.db"), "the database from the backup");
            StagedRestore.Arm(root, staged);

            var applied = StagedRestore.ApplyPending(root);

            Assert.Equal(["platform.db"], applied);
            Assert.Equal("the database from the backup", File.ReadAllText(live));
            // A restore that turns out to be the wrong one must not be the end
            // of the story.
            Assert.Equal("the database as it is now", File.ReadAllText(live + ".pre-restore"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void The_write_ahead_log_of_the_replaced_database_is_removed()
    {
        var root = Directory.CreateTempSubdirectory("deluno-restore").FullName;
        try
        {
            var live = Path.Combine(root, "platform.db");
            File.WriteAllText(live, "old");
            File.WriteAllText(live + "-wal", "journal belonging to the old file");
            File.WriteAllText(live + "-shm", "shared memory for the old file");

            var staged = Path.Combine(root, StagedRestore.StagingFolderName, "20260904-000000");
            Directory.CreateDirectory(staged);
            File.WriteAllText(Path.Combine(staged, "platform.db"), "restored");
            StagedRestore.Arm(root, staged);

            StagedRestore.ApplyPending(root);

            // Left behind, SQLite would replay a log belonging to a different
            // database over the one just restored - worse than not restoring.
            Assert.False(File.Exists(live + "-wal"));
            Assert.False(File.Exists(live + "-shm"));
            Assert.Equal("restored", File.ReadAllText(live));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Applying_is_a_no_op_when_nothing_is_staged()
    {
        var root = Directory.CreateTempSubdirectory("deluno-restore").FullName;
        try
        {
            // Runs on every start, so it has to be silent and cheap when there
            // is nothing to do.
            Assert.False(StagedRestore.IsPending(root));
            Assert.Empty(StagedRestore.ApplyPending(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_marker_that_outlived_its_folder_is_cleared_rather_than_retried_for_ever()
    {
        var root = Directory.CreateTempSubdirectory("deluno-restore").FullName;
        try
        {
            StagedRestore.Arm(root, Path.Combine(root, StagedRestore.StagingFolderName, "gone"));

            Assert.Empty(StagedRestore.ApplyPending(root));
            Assert.False(StagedRestore.IsPending(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Applying_it_twice_does_not_undo_work_done_since()
    {
        var root = Directory.CreateTempSubdirectory("deluno-restore").FullName;
        try
        {
            var live = Path.Combine(root, "platform.db");
            File.WriteAllText(live, "old");
            var staged = Path.Combine(root, StagedRestore.StagingFolderName, "20260904-000000");
            Directory.CreateDirectory(staged);
            File.WriteAllText(Path.Combine(staged, "platform.db"), "restored");
            StagedRestore.Arm(root, staged);

            StagedRestore.ApplyPending(root);
            File.WriteAllText(live, "work done after the restore");

            // The marker is consumed, so a later start must not silently roll
            // the database back to the backup again.
            Assert.Empty(StagedRestore.ApplyPending(root));
            Assert.Equal("work done after the restore", File.ReadAllText(live));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_staged_path_that_escapes_the_data_root_is_refused()
    {
        var root = Directory.CreateTempSubdirectory("deluno-restore").FullName;
        try
        {
            var staged = Path.Combine(root, StagedRestore.StagingFolderName, "20260904-000000");
            Directory.CreateDirectory(Path.Combine(staged, "nested"));
            File.WriteAllText(Path.Combine(staged, "nested", "fine.db"), "ok");
            StagedRestore.Arm(root, staged);

            var applied = StagedRestore.ApplyPending(root);

            Assert.Equal([Path.Combine("nested", "fine.db")], applied);
            Assert.True(File.Exists(Path.Combine(root, "nested", "fine.db")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
