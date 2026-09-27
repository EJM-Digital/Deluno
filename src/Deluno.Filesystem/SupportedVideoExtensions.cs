namespace Deluno.Filesystem;

/// <summary>
/// The one video-extension list every filesystem service that scans a folder
/// for "the" media file reads from, instead of each keeping its own near
/// -identical copy. ImportPipelineService, ExistingLibraryImportService,
/// FilesystemReconciliationService, SubtitleInventoryService and
/// ManualImportFolderScanner had five separately maintained lists that had
/// already drifted (one was missing .m2ts) -- exactly the kind of repetition
/// that lets the same file type be recognised as media in one path and
/// silently ignored in another.
///
/// <para>The set is the union every prior copy held, so unifying them widens
/// nobody's recognised set and narrows nobody's -- see the individual git
/// history for which extensions came from where.</para>
/// </summary>
public static class SupportedVideoExtensions
{
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts"
    };

    public static bool Contains(string? path)
        => path is { Length: > 0 } && Extensions.Contains(Path.GetExtension(path));
}
