using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Deluno.Tray;

/// <summary>The old folder that holds Deluno's data, and the new empty folder Deluno has been pointed at.</summary>
public sealed record DataFolderMoveOffer(string OldFolder, string NewFolder);

/// <summary>What a move did, in everyday words. <see cref="Moved"/> is false when nothing was switched.</summary>
public sealed record DataFolderMoveResult(bool Moved, string Words);

/// <summary>
/// DAT-02 (#1302): pointing Deluno at a new, empty data folder while the old one still holds its data used to start fresh without a
/// word. Now Deluno says the old folder has their data and offers two things: "Move my data here" (copy everything, check the copy,
/// then switch) or "Start fresh". Nothing is ever deleted from the old folder, and a new folder that already holds anything is never
/// written over.
/// </summary>
public static class DataFolderMove
{
    public const string MoveButton = "Move my data here";
    public const string StartFreshButton = "Start fresh";

    /// <summary>Folders inside a data folder that are not Deluno's data on their own (a backup or a log is no reason to offer a move).</summary>
    private static readonly string[] NotDataFolders = ["backups", "logs", "restore-staging"];

    /// <summary>
    /// The move to offer, or null. Offered only when the data folder is missing or empty and a different folder Deluno used before holds
    /// its data: the folder it last started on, or, for a settings file from before that was recorded, the default folders.
    /// </summary>
    public static DataFolderMoveOffer? FindOffer(AppSettings settings)
    {
        string newFolder;
        try
        {
            newFolder = Full(settings.DataRoot);
        }
        catch
        {
            return null;
        }

        if (!IsMissingOrEmpty(newFolder))
        {
            return null;
        }

        var paths = AppSettings.InspectPathState();
        IEnumerable<string> candidates = string.IsNullOrWhiteSpace(settings.LastDataRoot)
            ? [paths.DefaultDataRoot, paths.LegacyDefaultDataRoot]
            : [settings.LastDataRoot];
        foreach (var candidate in candidates)
        {
            try
            {
                var old = Full(candidate);
                if (!SameFolder(old, newFolder) && HoldsData(old))
                {
                    return new DataFolderMoveOffer(old, newFolder);
                }
            }
            catch
            {
                // A folder that cannot be looked at is not offered.
            }
        }

        return null;
    }

    /// <summary>True when the folder holds a Deluno database (outside backups and logs).</summary>
    public static bool HoldsData(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return false;
        }

        return EnumerateData(folder).Any();

        static IEnumerable<string> EnumerateData(string folder)
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.db", SearchOption.TopDirectoryOnly))
            {
                yield return file;
            }

            foreach (var sub in Directory.EnumerateDirectories(folder))
            {
                if (NotDataFolders.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(sub, "*.db", SearchOption.AllDirectories))
                {
                    yield return file;
                }
            }
        }
    }

    /// <summary>What the person is told while the question is open (and what a start with no one to ask says).</summary>
    public static string QuestionWords(DataFolderMoveOffer offer)
        => $"Your Deluno data (your libraries, connections and settings) is still in {offer.OldFolder}. Deluno has been pointed at {offer.NewFolder}, which is empty. "
         + $"\"{MoveButton}\" copies everything there, checks the copy and then switches to it. \"{StartFreshButton}\" starts empty in the new folder. "
         + $"Either way nothing is deleted from {offer.OldFolder}.";

    /// <summary>What a start with nobody to ask says (a Windows service has no window to put the question in).</summary>
    public static string PendingWords(DataFolderMoveOffer offer)
        => $"Deluno did not start, because your data is in {offer.OldFolder} and the folder it was pointed at ({offer.NewFolder}) is empty. "
         + $"Open Deluno from the tray and choose \"{MoveButton}\" or \"{StartFreshButton}\", or point Deluno back at {offer.OldFolder}.";

    /// <summary>"Start fresh": the new folder is where Deluno starts from now on. The old folder is not touched.</summary>
    public static string? StartFresh(DataFolderMoveOffer offer)
        => AppSettings.Update(s => s.LastDataRoot = offer.NewFolder);

    /// <summary>
    /// "Move my data here": copies everything in the old folder to the new one, checks every file (size and SHA-256) and that every
    /// database opens and passes SQLite's own check, then records the new folder as the one Deluno uses. Any failure removes only what this
    /// move put in the new folder and says what went wrong; the old folder is never changed.
    /// </summary>
    public static DataFolderMoveResult Move(DataFolderMoveOffer offer, Action<string>? progress = null)
        => Move(offer, progress, CopyOneFile, RecordSwitch);

    internal static DataFolderMoveResult Move(DataFolderMoveOffer offer, Action<string>? progress, Action<string, string> copyFile, Func<DataFolderMoveOffer, string?> recordSwitch)
    {
        var oldFolder = Full(offer.OldFolder);
        var newFolder = Full(offer.NewFolder);
        if (!Directory.Exists(oldFolder) || !HoldsData(oldFolder))
        {
            return Failed($"Deluno did not move anything: it can no longer find its data in {oldFolder}.", oldFolder);
        }

        if (!IsMissingOrEmpty(newFolder))
        {
            return Failed($"Deluno did not move anything: {newFolder} already has files in it, and Deluno never writes over a folder that has something in it. Choose an empty folder, or choose \"{StartFreshButton}\" to use it as it is.", oldFolder);
        }

        if (IsInside(newFolder, oldFolder) || IsInside(oldFolder, newFolder))
        {
            return Failed($"Deluno did not move anything: {newFolder} and {oldFolder} are inside each other. Choose a folder beside the old one instead.", oldFolder);
        }

        var madeFolder = !Directory.Exists(newFolder);
        var createdFiles = new List<string>();
        var createdFolders = new List<string>();
        try
        {
            if (madeFolder)
            {
                Directory.CreateDirectory(newFolder);
            }

            var files = Directory.EnumerateFiles(oldFolder, "*", SearchOption.AllDirectories).ToList();
            foreach (var folder in Directory.EnumerateDirectories(oldFolder, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(newFolder, Path.GetRelativePath(oldFolder, folder));
                if (!Directory.Exists(target))
                {
                    Directory.CreateDirectory(target);
                    createdFolders.Add(target);
                }
            }

            var done = 0;
            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(oldFolder, file);
                var target = Path.Combine(newFolder, relative);
                progress?.Invoke($"Copying your data ({++done} of {files.Count}): {relative}");
                copyFile(file, target);
                createdFiles.Add(target);
            }

            done = 0;
            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(oldFolder, file);
                var target = Path.Combine(newFolder, relative);
                progress?.Invoke($"Checking the copy ({++done} of {files.Count}): {relative}");
                var problem = CompareFiles(file, target, relative);
                if (problem is not null)
                {
                    throw new MoveProblem(problem);
                }
            }

            foreach (var database in createdFiles.Where(f => f.EndsWith(".db", StringComparison.OrdinalIgnoreCase)))
            {
                var relative = Path.GetRelativePath(newFolder, database);
                progress?.Invoke($"Opening the copied database {relative}");
                var problem = CheckDatabaseOpens(database, relative);
                if (problem is not null)
                {
                    throw new MoveProblem(problem);
                }
            }
        }
        catch (Exception ex)
        {
            RemoveWhatWasCopied(newFolder, madeFolder, createdFiles, createdFolders);
            var why = ex is MoveProblem ? ex.Message : $"{ex.Message.TrimEnd('.')}.";
            return Failed($"Deluno could not copy your data to {newFolder}: {why} ", oldFolder);
        }

        var switchProblem = recordSwitch(offer);
        if (switchProblem is not null)
        {
            RemoveWhatWasCopied(newFolder, madeFolder, createdFiles, createdFolders);
            return Failed($"Deluno copied your data but could not switch to {newFolder}: {switchProblem.TrimEnd('.')}. ", oldFolder);
        }

        return new DataFolderMoveResult(true, $"Deluno copied your data from {oldFolder} to {newFolder} and checked the copy. It now uses {newFolder}. Nothing was deleted from {oldFolder}; delete it yourself when you are happy.");
    }

    private sealed class MoveProblem(string message) : Exception(message);

    private static DataFolderMoveResult Failed(string words, string oldFolder)
        => new(false, $"{words.TrimEnd()} Nothing was changed: your data is still in {oldFolder}, and Deluno has not switched.");

    private static string? RecordSwitch(DataFolderMoveOffer offer)
        => AppSettings.Update(s => s.LastDataRoot = offer.NewFolder);

    private static void CopyOneFile(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to, overwrite: false);
    }

    private static string? CompareFiles(string original, string copy, string relative)
    {
        if (!File.Exists(copy))
        {
            return $"{relative} is missing from the copy.";
        }

        if (new FileInfo(original).Length != new FileInfo(copy).Length)
        {
            return $"{relative} was not copied in full (its size differs).";
        }

        return Hash(original) == Hash(copy) ? null : $"{relative} was copied wrongly (its contents differ).";
    }

    private static string Hash(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string? CheckDatabaseOpens(string database, string relative)
    {
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            var answer = Convert.ToString(command.ExecuteScalar());
            return string.Equals(answer, "ok", StringComparison.OrdinalIgnoreCase)
                ? null
                : $"the copied database {relative} did not pass SQLite's own check ({answer}).";
        }
        catch (Exception ex)
        {
            return $"the copied database {relative} would not open ({ex.Message.TrimEnd('.')}).";
        }
    }

    /// <summary>Removes only what this move created. A folder that was there empty stays; one this move made goes.</summary>
    private static void RemoveWhatWasCopied(string newFolder, bool madeFolder, List<string> files, List<string> folders)
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in files)
        {
            TryDelete(() => File.Delete(file));
            // A database opened for the check may have left its journal files beside it.
            foreach (var extra in new[] { file + "-wal", file + "-shm", file + "-journal" })
            {
                TryDelete(() => File.Delete(extra));
            }
        }

        foreach (var folder in folders.OrderByDescending(f => f.Length))
        {
            TryDelete(() => Directory.Delete(folder, recursive: true));
        }

        if (madeFolder)
        {
            TryDelete(() => Directory.Delete(newFolder, recursive: true));
        }

        static void TryDelete(Action delete)
        {
            try { delete(); } catch { /* best effort: it is only the copy that failed to clean up */ }
        }
    }

    private static bool IsMissingOrEmpty(string folder)
        => !Directory.Exists(folder) || !Directory.EnumerateFileSystemEntries(folder).Any();

    private static string Full(string path) => Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool SameFolder(string a, string b) => string.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsInside(string folder, string parent)
        => Full(folder).StartsWith(Full(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
