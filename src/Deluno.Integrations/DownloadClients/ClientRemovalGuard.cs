namespace Deluno.Integrations.DownloadClients;

/// <summary>Whether a download client has a download, as far as Deluno could tell.</summary>
internal enum ClientHas
{
    Yes,
    No,

    /// <summary>The client did not answer, or answered in a way that could not be read.</summary>
    Unknown
}

/// <summary>
/// One rule for every client that removes a download (#1212, #1201): a removal counts as done only when the
/// client had the download before the call and no longer has it after.
///
/// <para>SABnzbd answers <c>{"status":true}</c> to a history delete for an id it has never heard of, and a
/// client's "yes" says nothing about what it did. So the client is looked at, not believed: asked before the call
/// (a download it does not have is a plain failure, never "Done") and asked again after (a download it still has is
/// a failure, whatever the call said).</para>
/// </summary>
internal static class ClientRemovalGuard
{
    public static async Task<DownloadClientActionResult> RunAsync(
        Deluno.Connections.Contracts.DownloadClientItem client,
        string queueItemId,
        string action,
        string clientName,
        Func<Task<ClientHas>> has,
        Func<Task<DownloadClientActionResult>> remove)
    {
        switch (await has())
        {
            case ClientHas.No:
                return DownloadClientHelpers.ActionFailure(client, queueItemId, action,
                    $"{clientName} has no download with that id, so nothing was removed. It may already have been removed there.");
            case ClientHas.Unknown:
                return DownloadClientHelpers.ActionFailure(client, queueItemId, action,
                    $"{clientName} could not be asked whether it has this download, so nothing was changed.", category: "unreachable");
        }

        var result = await remove();
        if (!result.Succeeded)
        {
            return result;
        }

        return await has() switch
        {
            ClientHas.No => result,
            ClientHas.Yes => DownloadClientHelpers.ActionFailure(client, queueItemId, action,
                $"{clientName} still has this download after being told to remove it, so it was not removed."),
            _ => DownloadClientHelpers.ActionFailure(client, queueItemId, action,
                $"{clientName} did not confirm that the download is gone, so Deluno cannot say it was removed.", category: "unreachable")
        };
    }
}

/// <summary>
/// Removing the folder a finished download left on disk, for a client whose own delete does not (#1212). Deluno's
/// own delete, the same one it uses for every other download's files (an orphaned or refused payload, an rTorrent
/// download): the folder goes, extras and all, and anything that cannot go is said plainly.
/// </summary>
internal static class DownloadFolderRemoval
{
    /// <summary>
    /// Whether <paramref name="path"/> is one Deluno can safely delete on a download's behalf: named, resolvable, not
    /// a filesystem root, and present on this machine.
    /// </summary>
    public static bool IsSafeToDelete(string? path, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string resolved;
        try
        {
            resolved = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var root = Path.GetPathRoot(resolved);
        if (string.IsNullOrEmpty(root))
        {
            return false;
        }

        var trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var trimmedResolved = resolved.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmedResolved.Length == 0 || string.Equals(trimmedResolved, trimmedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Directory.Exists(resolved) && !File.Exists(resolved))
        {
            return false;
        }

        fullPath = resolved;
        return true;
    }

    /// <summary>
    /// Removes the folder (or file) a finished job left behind.
    /// </summary>
    /// <param name="path">Where the client says the finished files are.</param>
    /// <param name="jobName">
    /// The job's name. The folder is the job's own only when it carries that name; a client that points at a shared
    /// folder (a category folder, say) must never have it wiped on one job's behalf.
    /// </param>
    /// <param name="onThisMachine">Whether the client runs here, so a folder that is not found really is gone.</param>
    /// <returns>Null when the files are gone (removed now, or never there), else why they are still on disk.</returns>
    public static string? Remove(string? path, string? jobName, bool onThisMachine)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return onThisMachine ? null : "The client did not say where its files are, so Deluno could not look for them.";
        }

        if (!IsSafeToDelete(path, out var fullPath))
        {
            return onThisMachine
                ? null
                : $"The client is on another machine, so Deluno cannot see {path} to check it was removed.";
        }

        var trimmedFull = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var folderName = Path.GetFileName(trimmedFull);
        var isFolder = Directory.Exists(fullPath);

        // #1224: SABnzbd 5.1.3 names the finished FILE, not the job's folder. When that file sits in a folder
        // named after the job, the job's folder is what the job left, extras and all (.nfo, poster, Sample). It is
        // never a filesystem root, and never a shared folder such as the category: it must carry the job's name.
        var jobFolder = isFolder ? null : JobOwnFolder(trimmedFull, jobName);
        var target = jobFolder ?? fullPath;

        if (jobFolder is null)
        {
            var stem = isFolder ? folderName : Path.GetFileNameWithoutExtension(folderName);
            if (!string.IsNullOrWhiteSpace(jobName)
                && !stem.StartsWith(jobName.Trim(), StringComparison.OrdinalIgnoreCase)
                && !jobName.Trim().StartsWith(stem, StringComparison.OrdinalIgnoreCase))
            {
                return $"{fullPath} is not named after this download, so Deluno left it alone.";
            }
        }

        try
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
            else
            {
                File.Delete(target);
            }

            // Say "gone" only when nothing of the job is left.
            return File.Exists(target) || Directory.Exists(target)
                ? $"{target} is still on disk after Deluno tried to delete it."
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"Deluno could not delete {target}: {exception.Message}";
        }
    }

    /// <summary>
    /// The folder directly holding <paramref name="filePath"/> when it is the job's own: named after the job, and not
    /// a filesystem root or a top-level folder (a category or completed folder).
    /// </summary>
    internal static string? JobOwnFolder(string filePath, string? jobName)
    {
        var name = jobName?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var parent = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(parent))
        {
            return null;
        }

        var parentTrimmed = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parentName = Path.GetFileName(parentTrimmed);
        var grandparent = Path.GetDirectoryName(parentTrimmed);
        if (string.IsNullOrEmpty(parentName) || string.IsNullOrEmpty(grandparent))
        {
            return null;
        }

        // The completed area itself sits at least one level below a root, so the job folder has two parents' worth of path.
        if (string.IsNullOrEmpty(Path.GetDirectoryName(grandparent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))))
        {
            return null;
        }

        return string.Equals(parentName, name, StringComparison.OrdinalIgnoreCase)
            || parentName.StartsWith(name, StringComparison.OrdinalIgnoreCase)
            ? parentTrimmed
            : null;
    }

    /// <summary>
    /// Removes the empty temporary folders a client leaves beside a removed download, such as Transmission's
    /// <c>Name.mkv__ccMBKa</c> (#1224): inside <paramref name="downloadFolder"/>, named after the torrent plus
    /// <c>__</c> and six letters or digits, and holding no file at all.
    /// </summary>
    /// <returns>Null when nothing is left over, else why a leftover could not go.</returns>
    public static string? RemoveEmptyLeftovers(string? downloadFolder, string? torrentName)
    {
        if (string.IsNullOrWhiteSpace(downloadFolder) || string.IsNullOrWhiteSpace(torrentName)
            || !IsSafeToDelete(downloadFolder, out var folder) || !Directory.Exists(folder))
        {
            return null;
        }

        var prefix = torrentName.Trim() + "__";
        string? problem = null;
        try
        {
            foreach (var candidate in Directory.EnumerateDirectories(folder))
            {
                var leaf = Path.GetFileName(candidate);
                if (leaf.Length != prefix.Length + 6
                    || !leaf.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    || !leaf[prefix.Length..].All(char.IsAsciiLetterOrDigit)
                    || Directory.EnumerateFiles(candidate, "*", SearchOption.AllDirectories).Any())
                {
                    continue;
                }

                try
                {
                    Directory.Delete(candidate, recursive: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    problem = $"Deluno could not remove the empty folder {candidate}: {exception.Message}";
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"Deluno could not look for leftover folders in {folder}: {exception.Message}";
        }

        return problem;
    }
}
