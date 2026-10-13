using Deluno.Contracts;

namespace Deluno.Infrastructure.Storage;

/// <summary>
/// A restore that has been unpacked but not yet applied, and the moment it is
/// safe to apply one.
///
/// <para><b>Why a restore has to wait for a restart.</b> Deluno's databases are
/// open the whole time it runs, with WAL files beside them. Writing a backup
/// straight over them fails on Windows at the first file — confirmed on an
/// installed build, where a restore left one <c>cache.db.pre-restore</c> copy
/// and returned a 500, so the recovery path the documentation describes did not
/// work at all.</para>
///
/// <para>So the upload is unpacked into a staging folder and a marker is
/// written. <see cref="ApplyPending"/> runs before anything opens a database,
/// moves the staged files into place, and clears the marker. The window where
/// files are replaced is the one moment nothing is holding them.</para>
///
/// <para>It also removes the stale <c>-wal</c> and <c>-shm</c> files beside each
/// restored database. Those belong to the database that <em>was</em> there; left
/// behind, SQLite would replay a write-ahead log belonging to a different file
/// over the one just restored, which is a worse outcome than not restoring.</para>
/// </summary>
public static class StagedRestore
{
    public const string StagingFolderName = "restore-staging";

    /// <summary>The marker naming the staged folder to apply next start.</summary>
    private const string MarkerFileName = "pending-restore.txt";

    private static string MarkerPath(string dataRoot)
        => Path.Combine(Path.GetFullPath(dataRoot), StagingFolderName, MarkerFileName);

    /// <summary>Records that this staged folder should be applied on next start.</summary>
    public static void Arm(string dataRoot, string stagedFolder, IStateFileSystem? io = null)
    {
        var marker = MarkerPath(dataRoot);
        // Atomic (#1260): a marker killed half-written would otherwise name a folder that is not there.
        if (StateFile.Write(marker, Path.GetFullPath(stagedFolder), io) is { } problem)
        {
            throw new StateFileException($"The restore could not be set up to apply at the next start: {problem}.");
        }
    }

    /// <summary>Whether a restore is waiting to be applied.</summary>
    public static bool IsPending(string dataRoot) => File.Exists(MarkerPath(dataRoot));

    /// <summary>
    /// Applies a staged restore, if one is waiting, and returns what it moved.
    ///
    /// <para>Call this before opening any database. It is deliberately quiet
    /// when nothing is staged: it runs on every start.</para>
    /// </summary>
    public static IReadOnlyList<string> ApplyPending(string dataRoot, Action<string>? log = null, IStateFileSystem? io = null)
    {
        var root = Path.GetFullPath(dataRoot);
        var marker = MarkerPath(root);
        var read = StateFile.ReadText(marker, io);
        if (read.Kind == StateFileKind.Missing)
        {
            return [];
        }

        if (read.Kind == StateFileKind.Busy)
        {
            // Not a failed start (#1260): the restore waits for the next one, with the marker and the staged folder untouched.
            log?.Invoke("A restore is waiting to be applied, but its marker was in use by something else, so it was left for the next start. Nothing was changed.");
            return [];
        }

        var staged = read.Value ?? string.Empty;
        if (read.Kind == StateFileKind.Unreadable || !Directory.Exists(staged))
        {
            var stagedFolders = Directory.Exists(Path.GetDirectoryName(marker)!)
                ? Directory.GetDirectories(Path.GetDirectoryName(marker)!)
                : [];
            if (read.Kind == StateFileKind.Unreadable || stagedFolders.Length > 0)
            {
                // A marker that is empty, or names a folder that is not there while a staged restore is: it was cut short. Dropping it
                // would silently lose the restore, so it is kept and said in plain words; the person can apply or remove it by hand.
                log?.Invoke($"A restore is waiting to be applied, but its marker ({marker}) could not be understood, so it was not applied and nothing was removed. The staged files are in {Path.GetDirectoryName(marker)}; delete that folder to cancel the restore, or restore the backup again.");
                return [];
            }

            // The marker outlived its folder, and nothing else is staged. Clearing it is the only sensible
            // move: leaving it means trying and failing on every start for ever.
            File.Delete(marker);
            return [];
        }

        var applied = new List<string>();
        foreach (var source in Directory.EnumerateFiles(staged, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(staged, source);
            var target = Path.GetFullPath(Path.Combine(root, relative));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            // Kept, so a restore that turns out to be the wrong one is not the
            // end of the story.
            if (File.Exists(target))
            {
                File.Copy(target, target + ".pre-restore", overwrite: true);
            }

            File.Copy(source, target, overwrite: true);

            // The journal of the database that was here, not the one now here.
            foreach (var stale in new[] { target + "-wal", target + "-shm" })
            {
                if (File.Exists(stale))
                {
                    File.Delete(stale);
                }
            }

            applied.Add(relative);
        }

        File.Delete(marker);
        try
        {
            Directory.Delete(staged, recursive: true);
        }
        catch (IOException)
        {
            // The files are in place, which is what mattered. A staging folder
            // that could not be swept is untidy, not broken.
        }

        return applied;
    }
}
