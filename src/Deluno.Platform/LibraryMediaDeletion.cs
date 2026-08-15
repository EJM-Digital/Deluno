using Deluno.Platform.Contracts;

namespace Deluno.Platform;

/// <summary>
/// Deletes a title's imported files only when every target is inside a configured
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

    public static LibraryMediaDeletionResult Delete(
        IReadOnlyList<TrackedLibraryFile> trackedFiles,
        IReadOnlyList<LibraryItem> libraries,
        CancellationToken cancellationToken)
    {
        var librariesById = libraries.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var deletedFiles = 0;
        var deletedFolders = 0;
        var warnings = new List<string>();

        foreach (var group in trackedFiles.GroupBy(item => item.LibraryId, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!librariesById.TryGetValue(group.Key, out var library) || string.IsNullOrWhiteSpace(library.RootPath))
            {
                warnings.Add("A tracked path was skipped because its library root is no longer configured.");
                continue;
            }

            string rootPath;
            try
            {
                rootPath = Path.GetFullPath(library.RootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                warnings.Add($"{library.Name}: the configured library root could not be read.");
                continue;
            }

            var paths = group
                .Select(item => TryGetSafePath(item.FilePath, rootPath, out var path) ? path : null)
                .Where(path => path is not null)
                .Cast<string>()
                .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .ToArray();

            if (paths.Length != group.Count())
            {
                warnings.Add($"{library.Name}: a tracked path was skipped because it is outside the library root.");
            }

            var deletedByFolder = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var folder in paths
                         .Select(path => TryGetTitleFolder(path, rootPath))
                         .Where(folder => folder is not null)
                         .Cast<string>()
                         .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!Directory.Exists(folder)) continue;
                    var filesInFolder = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Count();
                    Directory.Delete(folder, recursive: true);
                    deletedFiles += filesInFolder;
                    deletedFolders++;
                    deletedByFolder.Add(folder);
                }
                catch
                {
                    warnings.Add($"{library.Name}: Deluno could not remove {folder}.");
                }
            }

            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (deletedByFolder.Any(folder => IsInsideRoot(path, folder))) continue;

                try
                {
                    if (!File.Exists(path)) continue;
                    File.Delete(path);
                    deletedFiles++;
                }
                catch
                {
                    warnings.Add($"{library.Name}: Deluno could not remove {path}.");
                }
            }
        }

        return new LibraryMediaDeletionResult(deletedFiles, deletedFolders, warnings.Distinct().ToArray());
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
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.StartsWith(root + Path.DirectorySeparatorChar, comparison) ||
               string.Equals(path, root, comparison);
    }
}

public sealed record TrackedLibraryFile(string LibraryId, string FilePath);

public sealed record LibraryMediaDeletionResult(
    int DeletedFileCount,
    int DeletedFolderCount,
    IReadOnlyList<string> Warnings);

public sealed record LibraryMediaDeletionPreview(
    IReadOnlyList<string> FilePaths,
    IReadOnlyList<string> FolderPaths,
    IReadOnlyList<string> Warnings);
