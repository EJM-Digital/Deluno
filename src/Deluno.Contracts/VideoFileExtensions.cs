namespace Deluno.Contracts;

/// <summary>
/// The one list of file extensions Deluno treats as video. Every code path that judges a file to be video (imports, the
/// processor's output checks, title folders, file facts, repairs, path safety) reads this set; none keeps its own copy,
/// so the same file is never imported by one path and ignored by another (#1409).
/// <c>VideoExtensionListsTests</c> fails if any source file lists its own. A disc image (.iso) and a DVD fragment (.vob) are not
/// a film file and are never imported as one.
/// </summary>
public static class VideoFileExtensions
{
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts", ".mts",
        ".mpg", ".mpeg", ".webm", ".flv", ".ogm", ".ogv", ".divx", ".3gp"
    };

    /// <summary>True when the path (or bare extension, with its dot) names a video file.</summary>
    public static bool Contains(string? pathOrExtension)
        => pathOrExtension is { Length: > 0 } && Extensions.Contains(Path.GetExtension(pathOrExtension));

    /// <summary>
    /// The extension a file name ends with when it is a video one (lower case, with its dot), otherwise null.
    /// Used to strip a trailing video extension from a release name without touching the dots inside it.
    /// </summary>
    public static string? TrailingVideoExtension(string? name)
    {
        var extension = Path.GetExtension(name ?? string.Empty);
        return extension.Length > 0 && Extensions.Contains(extension) ? extension : null;
    }
}
