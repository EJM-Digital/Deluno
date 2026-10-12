namespace Deluno.Filesystem;

/// <summary>
/// What a path says about a release (#1255): its own name first. Quality, resolution and source are read from
/// names by substring, so a library or download folder called "2160p", or a test's random temp folder that
/// happened to contain 2160, turned a 1080p WEB release into "WEB 2160p" and changed the name it was filed under.
/// Only when the name says nothing does the one folder around a video file speak for it (a multi-file torrent
/// arrives as "Title.1080p.WEB/movie.mkv"), and never the folders above that.
/// Anything that reads quality, resolution, source or codec out of a path goes through here.
/// </summary>
internal static class ReleasePathEvidence
{
    public static string FromPaths(Func<string?, string?> detectQuality, params string?[] paths)
    {
        var own = Join(paths, withParentFolder: false);
        return detectQuality(own) is null ? Join(paths, withParentFolder: true) : own;
    }

    private static string Join(string?[] paths, bool withParentFolder)
        => string.Join(' ', paths.Select(path => Tail(path, withParentFolder)).Where(part => part.Length > 0));

    private static string Tail(string? path, bool withParentFolder)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var segments = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        var takeParent = withParentFolder && segments.Length > 0 && SupportedVideoExtensions.Contains(segments[^1]);
        return string.Join(' ', segments.TakeLast(takeParent ? 2 : 1));
    }
}
