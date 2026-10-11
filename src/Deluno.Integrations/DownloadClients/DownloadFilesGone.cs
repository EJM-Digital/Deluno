namespace Deluno.Integrations.DownloadClients;

/// <summary>
/// One judgement of "the files of this finished download are gone" for every place that asks (#1200, #1210): the
/// Transfers check that flags the download and the planner that must not hand a missing path to a processor.
/// </summary>
public static class DownloadFilesGone
{
    public const string Message = "The download client lists this as done, but its files are no longer on disk. Remove it from the client, or search for another copy.";

    /// <summary>
    /// The statuses a finished download can be in while its files are expected on disk: ready to import, done, or
    /// handed (or about to be handed) to a processor.
    /// </summary>
    public static bool ExpectsItsFiles(string? status) =>
        status is DownloadQueueStatuses.ImportReady
            or DownloadQueueStatuses.Completed
            or DownloadQueueStatuses.WaitingForProcessor
            or DownloadQueueStatuses.NotHandedOver;

    /// <summary>
    /// True only for a path that is not there while a folder above it, one or two levels up, is. A path on another
    /// machine, unmapped, is not "gone", it is simply out of sight. Two levels, because SABnzbd keeps a finished job's
    /// file inside the job's own folder, and that folder goes with the file (rc.30.22).
    /// </summary>
    public static bool AreGone(string? sourcePath, Func<string, bool> pathExists)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !Path.IsPathRooted(sourcePath) || pathExists(sourcePath))
        {
            return false;
        }

        var parent = Path.GetDirectoryName(sourcePath.TrimEnd((char)92, (char)47));
        if (string.IsNullOrWhiteSpace(parent))
        {
            return false;
        }

        if (pathExists(parent))
        {
            return true;
        }

        var grandparent = Path.GetDirectoryName(parent.TrimEnd((char)92, (char)47));
        return !string.IsNullOrWhiteSpace(grandparent) && pathExists(grandparent);
    }

    public static bool AreGoneOnDisk(string? sourcePath) =>
        AreGone(sourcePath, path => File.Exists(path) || Directory.Exists(path));
}
