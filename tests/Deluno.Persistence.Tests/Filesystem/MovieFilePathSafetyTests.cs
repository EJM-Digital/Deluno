using Deluno.Filesystem;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>
/// #782: the per-file edit and delete actions on the Files tab take a path
/// from the client, so this is the one thing standing between "delete this
/// title's sample file" and "delete anything on the host the process can
/// reach". Pinned directly rather than only through the endpoints that use it.
/// </summary>
public sealed class MovieFilePathSafetyTests
{
    [Fact]
    public void Accepts_a_file_directly_inside_the_folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "library", "Dune Part Two (2024)");
        var requested = Path.Combine(folder, "Dune.Part.Two.2024.mkv");

        Assert.True(MovieFilePathSafety.TryResolveWithinFolder(folder, requested, out var resolved));
        Assert.Equal(Path.GetFullPath(requested), resolved);
    }

    [Fact]
    public void Accepts_a_file_in_a_subfolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "library", "Dune Part Two (2024)");
        var requested = Path.Combine(folder, "Subs", "Dune.en.srt");

        Assert.True(MovieFilePathSafety.TryResolveWithinFolder(folder, requested, out _));
    }

    [Fact]
    public void Refuses_a_path_outside_the_folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "library", "Dune Part Two (2024)");
        var requested = Path.Combine(Path.GetTempPath(), "library", "Some Other Movie (2020)", "file.mkv");

        Assert.False(MovieFilePathSafety.TryResolveWithinFolder(folder, requested, out _));
    }

    /// <summary>
    /// The classic escape: a sibling folder whose name starts with the same
    /// characters. A plain `StartsWith(folder)` check (without the trailing
    /// separator) would let this through.
    /// </summary>
    [Fact]
    public void Refuses_a_sibling_folder_whose_name_is_a_prefix_of_this_ones()
    {
        var folder = Path.Combine(Path.GetTempPath(), "library", "Dune");
        var requested = Path.Combine(Path.GetTempPath(), "library", "Dune Part Two (2024)", "file.mkv");

        Assert.False(MovieFilePathSafety.TryResolveWithinFolder(folder, requested, out _));
    }

    [Fact]
    public void Refuses_a_relative_escape_via_dot_dot_segments()
    {
        var folder = Path.Combine(Path.GetTempPath(), "library", "Dune Part Two (2024)");
        var requested = Path.Combine(folder, "..", "..", "etc", "passwd");

        Assert.False(MovieFilePathSafety.TryResolveWithinFolder(folder, requested, out _));
    }

    [Fact]
    public void Refuses_a_null_or_empty_request()
    {
        var folder = Path.Combine(Path.GetTempPath(), "library", "Dune Part Two (2024)");

        Assert.False(MovieFilePathSafety.TryResolveWithinFolder(folder, null, out _));
        Assert.False(MovieFilePathSafety.TryResolveWithinFolder(folder, "", out _));
        Assert.False(MovieFilePathSafety.TryResolveWithinFolder(folder, "   ", out _));
    }
}
