using Deluno.Libraries.Contracts;
using Deluno.Platform.Contracts;

namespace Deluno.Platform;

/// <summary>
/// Previews what removing a title would take (#1188: it never deletes anything; removal
/// goes through the recycle bin) when every target is inside a configured
/// library root. A title folder is removed as a unit only when nothing else is left
/// in it (#1317); otherwise the title's own files and sidecars go on their own.
/// </summary>
public static class LibraryMediaDeletion
{
    public static LibraryMediaDeletionPreview Preview(
        IReadOnlyList<TrackedLibraryFile> trackedFiles,
        IReadOnlyList<LibraryItem> libraries)
    {
        var librariesById = libraries.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var filePaths = new List<string>();
        var folderPaths = new List<string>();
        var warnings = new List<string>();
        var ownByLibrary = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var trackedFile in trackedFiles)
        {
            if (!librariesById.TryGetValue(trackedFile.LibraryId, out var library) || string.IsNullOrWhiteSpace(library.RootPath))
            {
                warnings.Add("A tracked path was skipped because its library root is no longer configured.");
                continue;
            }

            try
            {
                var rootPath = Path.GetFullPath(library.RootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!TryGetSafePath(trackedFile.FilePath, rootPath, out var fullPath))
                {
                    warnings.Add($"{library.Name}: a tracked path was skipped because it is outside the library root.");
                    continue;
                }

                if (File.Exists(fullPath))
                {
                    filePaths.Add(fullPath);
                    if (!ownByLibrary.TryGetValue(library.Id, out var own)) ownByLibrary[library.Id] = own = [];
                    own.Add(fullPath);
                }
            }
            catch
            {
                warnings.Add($"{library.Name}: the configured library root could not be read.");
            }
        }

        // #1317: the same rule the move itself follows, so the preview names what will really leave.
        foreach (var (libraryId, paths) in ownByLibrary)
        {
            try
            {
                foreach (var unit in TitleOwnFiles.Plan(librariesById[libraryId].RootPath!, paths))
                {
                    if (unit.IsDirectory) folderPaths.Add(unit.Path);
                    else if (!filePaths.Contains(unit.Path, StringComparer.OrdinalIgnoreCase)) filePaths.Add(unit.Path);
                }
            }
            catch
            {
                warnings.Add($"{librariesById[libraryId].Name}: the configured library root could not be read.");
            }
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return new LibraryMediaDeletionPreview(
            filePaths.Distinct(comparer).OrderBy(path => path, comparer).ToArray(),
            folderPaths.Distinct(comparer).OrderBy(path => path, comparer).ToArray(),
            warnings.Distinct().ToArray());
    }

    private static bool TryGetSafePath(string path, string rootPath, out string fullPath)
    {
        fullPath = string.Empty;
        try
        {
            var candidate = Path.GetFullPath(path);
            if (!IsInsideRoot(candidate, rootPath)) return false;
            fullPath = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsInsideRoot(string path, string rootPath)
    {
        return Deluno.Contracts.SafePath.IsInside(rootPath, path, allowEqual: true);
    }
}

public sealed record TrackedLibraryFile(string LibraryId, string FilePath);


public sealed record LibraryMediaDeletionPreview(
    IReadOnlyList<string> FilePaths,
    IReadOnlyList<string> FolderPaths,
    IReadOnlyList<string> Warnings);
