namespace Deluno.Platform;

/// <summary>
/// #1067 / #1078: what to do when a folder has already been moved on disk but Deluno's records
/// could not follow (a locked database, a full disk). The folder is put back if it can be;
/// otherwise the message says exactly where it is, so nothing is left renamed and unexplained.
/// </summary>
public static class FolderMoveRecovery
{
    /// <summary>Tries to move the folder back. False means it is still at the new place.</summary>
    public static bool TryPutBack(string source, string destination)
    {
        try
        {
            Directory.Move(destination, source);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string Describe(string title, string source, string destination, bool putBack, string reason, string? whereToFix = null)
        => putBack
            ? $"{title}: Deluno could not update its records for the move, so the folder was put back and nothing changed."
            : $"{title}: the folder is now at {destination}, but Deluno could not update its records ({reason}). Move it back to {source}{(string.IsNullOrEmpty(whereToFix) ? "" : $", or {whereToFix}")}.";

    /// <summary>
    /// Why a whole-folder move failed on disk, in plain words. A folder cannot be moved to another
    /// drive in one step, which is the usual cause.
    /// </summary>
    public static string DescribeDiskFailure(string title, string source, string destination, Exception exception)
    {
        if (exception is UnauthorizedAccessException)
        {
            return $"{title}: Deluno is not allowed to move the folder {source}. Check its permissions and try again.";
        }

        var sourceRoot = Path.GetPathRoot(Path.GetFullPath(source));
        var destinationRoot = Path.GetPathRoot(Path.GetFullPath(destination));
        if (!string.Equals(sourceRoot, destinationRoot, StringComparison.OrdinalIgnoreCase))
        {
            return $"{title}: the folder is on another drive ({sourceRoot}) from the new library ({destinationRoot}), and Deluno moves a whole folder only within one drive, so nothing was moved. Move the folder yourself, then run Rescan folder on the new library.";
        }

        return $"{title}: the folder could not be moved ({exception.Message}), so nothing was changed.";
    }
}
