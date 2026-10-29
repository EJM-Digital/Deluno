using Deluno.Contracts;

namespace Deluno.Filesystem;

/// <summary>
/// The filesystem services' name for the shared list of video extensions, <see cref="VideoFileExtensions"/>; it holds
/// no list of its own (#1409).
/// </summary>
public static class SupportedVideoExtensions
{
    public static IReadOnlySet<string> Extensions => VideoFileExtensions.Extensions;

    public static bool Contains(string? path) => VideoFileExtensions.Contains(path);
}
