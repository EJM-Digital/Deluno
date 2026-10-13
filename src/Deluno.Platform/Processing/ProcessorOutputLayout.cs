using Deluno.Platform.Contracts;

namespace Deluno.Platform.Processing;

/// <summary>Where a processor puts the cleaned copy of a source (#1262). Stub: filled in by the fix.</summary>
public static class ProcessorOutputLayout
{
    public static string? WatchedFolderFor(
        string libraryId,
        string? downloadsPath,
        IEnumerable<ProcessorLibraryFolder> folders,
        IReadOnlyList<ProcessorPathMappingItem> mappings)
        => downloadsPath;

    public static bool ClaimsTopLevelName(string sourcePath, string? watchedFolder, string name)
    {
        var trimmed = sourcePath.Replace('\\', '/').TrimEnd('/');
        var leaf = trimmed[(trimmed.LastIndexOf('/') + 1)..];
        return string.Equals(name, leaf, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, Path.GetFileNameWithoutExtension(leaf), StringComparison.OrdinalIgnoreCase);
    }
}
