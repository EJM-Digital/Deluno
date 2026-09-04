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
    public static void Arm(string dataRoot, string stagedFolder)
    {
        var marker = MarkerPath(dataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, Path.GetFullPath(stagedFolder));
    }

    /// <summary>Whether a restore is waiting to be applied.</summary>
    public static bool IsPending(string dataRoot) => File.Exists(MarkerPath(dataRoot));

    /// <summary>
    /// Applies a staged restore, if one is waiting, and returns what it moved.
    ///
    /// <para>Call this before opening any database. It is deliberately quiet
    /// when nothing is staged: it runs on every start.</para>
    /// </summary>
    public static IReadOnlyList<string> ApplyPending(string dataRoot)
    {
        var root = Path.GetFullPath(dataRoot);
        var marker = MarkerPath(root);
        if (!File.Exists(marker))
        {
            return [];
        }

        var staged = File.ReadAllText(marker).Trim();
        if (string.IsNullOrWhiteSpace(staged) || !Directory.Exists(staged))
        {
            // The marker outlived its folder. Clearing it is the only sensible
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
