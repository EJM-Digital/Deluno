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

    [Fact]
    public void A_symlink_inside_the_folder_that_leads_outside_is_refused()
    {
        var root = Path.Combine(Path.GetTempPath(), "deluno-links-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "Dune");
        var outside = Path.Combine(root, "Elsewhere");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.mkv"), "x");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(folder, "link"), outside);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return; // no privilege to create links on this machine
            }

            Assert.False(MovieFilePathSafety.TryResolveWithinFolder(folder, Path.Combine(folder, "link", "secret.mkv"), out _));
            Assert.True(MovieFilePathSafety.TryResolveWithinFolder(folder, Path.Combine(folder, "real.mkv"), out _));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_film_filed_directly_in_the_library_root_is_detected_as_flat()
    {
        var root = Path.Combine(Path.GetTempPath(), "movies");
        Assert.True(MovieFilePathSafety.IsFiledFlat(Path.Combine(root, "Heat (1995).mkv"), [root + Path.DirectorySeparatorChar]));
        Assert.False(MovieFilePathSafety.IsFiledFlat(Path.Combine(root, "Heat (1995)", "Heat (1995).mkv"), [root]));
    }

    [Theory]
    [InlineData("Heat (1995).mkv", true)]
    [InlineData("Heat (1995).en.srt", true)]
    [InlineData("Heat (1995).en.forced.srt", true)]
    [InlineData("Heat (1995).nfo", true)]
    [InlineData("Heat (1995)-poster.jpg", true)]
    [InlineData("Heat (1995).sample.mkv", false)]
    [InlineData("Ronin (1998).mkv", false)]
    [InlineData("Ronin (1998).en.srt", false)]
    [InlineData("Heat (1995) Extended.en.srt", false)]
    [InlineData("Heat (1995).Extended.en.srt", false)]
    [InlineData("Heat (1995).Directors.Cut.mkv", false)]
    public void A_flat_film_only_owns_its_own_named_files(string fileName, bool expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "movies");
        var primary = Path.Combine(root, "Heat (1995).mkv");

        Assert.Equal(expected, MovieFilePathSafety.BelongsToFlatFilm(primary, Path.Combine(root, fileName)));
    }

    [Fact]
    public void A_flat_film_does_not_own_files_in_a_subfolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "movies");
        Assert.False(MovieFilePathSafety.BelongsToFlatFilm(
            Path.Combine(root, "Heat (1995).mkv"),
            Path.Combine(root, "Other", "Heat (1995).en.srt")));
    }

    [Theory]
    [InlineData("Heat (1995).en.hearing_impaired.srt", true)]
    [InlineData("Heat (1995).foreign.srt", true)]
    [InlineData("Heat (1995).English.srt", true)]
    [InlineData("Heat (1995).pt-BR.srt", true)]
    [InlineData("Heat (1995).Man.srt", false)]
    [InlineData("Heat (1995).Extra.srt", false)]
    public void A_flat_film_reads_subtitle_names_with_the_subtitle_scans_own_rule(string fileName, bool expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "movies");
        Assert.Equal(expected, MovieFilePathSafety.BelongsToFlatFilm(Path.Combine(root, "Heat (1995).mkv"), Path.Combine(root, fileName)));
    }

    [Fact]
    public void A_companion_without_the_quality_tag_belongs_when_the_title_year_base_is_offered()
    {
        var root = Path.Combine(Path.GetTempPath(), "movies");
        var primary = Path.Combine(root, "Movie (2020) - 1080p.mkv");
        var nfo = Path.Combine(root, "Movie (2020).nfo");
        Assert.False(MovieFilePathSafety.BelongsToFlatFilm(primary, nfo));
        Assert.True(MovieFilePathSafety.BelongsToFlatFilm(primary, nfo, ["Movie (2020)"]));
    }

    [Fact]
    public void A_folder_holding_another_tracked_file_is_shared_but_a_folder_of_its_own_is_not()
    {
        var root = Path.Combine(Path.GetTempPath(), "movies");
        var heat = Path.Combine(root, "Action", "Heat.mkv");
        Assert.True(MovieFilePathSafety.IsSharedFolder(heat, [root], [Path.Combine(root, "Action", "Ronin.mkv")]));
        Assert.True(MovieFilePathSafety.IsSharedFolder(heat, [root], [Path.Combine(root, "Action", "S01E01.mkv")]));
        Assert.False(MovieFilePathSafety.IsSharedFolder(heat, [root], [Path.Combine(root, "Drama", "Ronin.mkv"), heat]));
        Assert.True(MovieFilePathSafety.IsSharedFolder(heat, [root], [], libraryStillExists: false));
    }

    [Fact]
    public void A_path_the_system_cannot_read_as_a_path_is_refused_not_thrown()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Dune");
        Assert.False(MovieFilePathSafety.TryResolveWithinFolder(folder, "bad\u0000name.mkv", out var resolved));
        Assert.Equal(string.Empty, resolved);
        Assert.False(MovieFilePathSafety.TryResolveWithinFolder("bad\u0000folder", Path.Combine(folder, "a.mkv"), out _));
    }

    [Fact]
    public void A_file_with_no_folder_is_not_filed_flat_and_its_folder_counts_as_shared()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.False(MovieFilePathSafety.IsFiledFlat(root, [Path.GetTempPath()]));
        Assert.True(MovieFilePathSafety.IsSharedFolder(root, [Path.GetTempPath()], []));
    }

    [Fact]
    public void A_tracked_path_that_cannot_be_read_is_skipped_when_judging_whether_a_folder_is_shared()
    {
        var root = Path.Combine(Path.GetTempPath(), "movies");
        var heat = Path.Combine(root, "Heat (1995)", "Heat.mkv");
        Assert.False(MovieFilePathSafety.IsSharedFolder(heat, [root], ["bad\u0000name.mkv", "", Path.Combine(root, "Ronin (1998)", "Ronin.mkv")]));
    }
}
