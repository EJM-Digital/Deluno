namespace Deluno.Filesystem;

/// <summary>
/// Cleans up what a processor left behind for one hand-off's own working
/// folder when that hand-off is abandoned instead of imported normally --
/// "give up" (stalled, timed out, the processor reporting failure, or a
/// manual-review release) and "import the original" alike (#953).
///
/// <para>Unlike <c>ImportPipelineService.CleanupProcessorHandoffLeftovers</c>,
/// which runs after a matched file has already been imported and so is free
/// to remove the whole folder, nothing here has been imported: a file the
/// processor actually finished could still be sitting in that folder, and
/// the processor-output safety net (#921) offers it on System &#8594; Health
/// for a person to import or delete. Deleting it out from under that screen
/// would be losing a finished download, not cleaning up -- so only what is
/// still being written (a temporary or partial file the processor never
/// finished renaming away) is removed, together with any folder that is left
/// empty by doing so. A complete, stable file is never touched here.</para>
/// </summary>
public static class ProcessorHandoffOutputCleanup
{
    /// <summary>
    /// Removes the not-ready leftovers under whichever of <paramref name="candidateFolderNames"/>
    /// exists as a folder directly under <paramref name="processorOutputRoot"/>, and any folder
    /// (including that top folder) left empty by doing so.
    /// </summary>
    /// <returns>How many leftover items were removed.</returns>
    public static int CleanupAbandonedHandoffOutput(
        IReadOnlyList<string> candidateFolderNames,
        string? processorOutputRoot,
        Func<string, bool> isReady,
        IReadOnlyList<Deluno.Libraries.Contracts.LibraryItem>? libraries = null)
    {
        if (string.IsNullOrWhiteSpace(processorOutputRoot) || !Directory.Exists(processorOutputRoot) || candidateFolderNames.Count == 0)
        {
            return 0;
        }

        // #1325: nothing is removed while a folder overlaps a library, and never a library's root, anything inside one, or a folder that holds one.
        if (libraries is not null
            && (Deluno.Libraries.LibraryFolderLayout.CleanupBlockedReason(libraries) is not null
                || Deluno.Libraries.LibraryFolderLayout.ReachesALibrary(libraries, processorOutputRoot)))
        {
            return 0;
        }

        var root = Path.GetFullPath(processorOutputRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var removed = 0;

        foreach (var name in candidateFolderNames)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var candidatePath = Path.GetFullPath(Path.Combine(root, name));
            if (!IsWithinPath(root, candidatePath) || !Directory.Exists(candidatePath)
                || (libraries is not null && Deluno.Libraries.LibraryFolderLayout.ReachesALibrary(libraries, candidatePath)))
            {
                continue;
            }

            removed += CleanupFolderKeepingReadyFiles(candidatePath, isReady);
        }

        return removed;
    }

    private static int CleanupFolderKeepingReadyFiles(string folder, Func<string, bool> isReady)
    {
        var removed = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                if (isReady(file))
                {
                    // A complete file: #921's Health row still offers it. Leaving
                    // it means leaving its containing folder too, below.
                    continue;
                }

                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            // Deepest first, so a folder only counts as empty once its own
            // now-empty children are already gone.
            foreach (var directory in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories)
                         .OrderByDescending(path => path.Length))
            {
                RemoveIfEmpty(directory);
            }

            RemoveIfEmpty(folder);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return removed;
    }

    private static void RemoveIfEmpty(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal static bool IsWithinPath(string root, string candidate)
        => Deluno.Contracts.SafePath.IsInside(root, candidate);

    private static StringComparison GetPathComparison()
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
