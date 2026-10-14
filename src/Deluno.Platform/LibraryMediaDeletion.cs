using Deluno.Libraries.Contracts;
using Deluno.Platform.Contracts;

namespace Deluno.Platform;

/// <summary>
/// Previews what removing a title would take (#1188: it never deletes anything; removal
/// goes through the recycle bin) when every target is inside a configured
/// library root. A title folder is removed as a unit when it is a direct child of
/// that root, matching the way media libraries are laid out.
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

                if (File.Exists(fullPath)) filePaths.Add(fullPath);
                var titleFolder = TryGetTitleFolder(fullPath, rootPath);
                if (titleFolder is not null && Directory.Exists(titleFolder)) folderPaths.Add(titleFolder);
            }
            catch
            {
                warnings.Add($"{library.Name}: the configured library root could not be read.");
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

    private static string? TryGetTitleFolder(string path, string rootPath)
    {
        var relative = Path.GetRelativePath(rootPath, path);
        var firstSeparator = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        return firstSeparator > 0 ? Path.Combine(rootPath, relative[..firstSeparator]) : null;
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
