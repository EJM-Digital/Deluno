using Deluno.Platform;

namespace Deluno.Platform.Tests;

/// <summary>#1067: the shared guard that decides which folder, if any, is a title's own.</summary>
public sealed class TitleFolderGuardTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "guard-lib", "Films");
    private static readonly string[] Roots = [Root];

    private static Task<bool> Nobody(string folder, CancellationToken ct) => Task.FromResult(false);
    private static Task<bool> Somebody(string folder, CancellationToken ct) => Task.FromResult(true);

    [Fact]
    public async Task A_movie_in_its_own_folder_under_the_library_is_renameable()
    {
        var folder = Path.Combine(Root, "Dune");
        var target = await TitleFolderGuard.ForMovieAsync(Path.Combine(folder, "Dune.mkv"), Roots, Nobody, default);

        Assert.True(target.IsSafe);
        Assert.Equal(folder, target.Folder);
    }

    [Fact]
    public async Task A_flat_movie_targets_no_folder_because_its_folder_is_the_library_root()
    {
        var target = await TitleFolderGuard.ForMovieAsync(Path.Combine(Root, "Dune.mkv"), Roots, Nobody, default);

        Assert.False(target.IsSafe);
        Assert.Equal("flat-layout", target.Code);
        Assert.Contains("no folder to rename", target.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_library_root_with_a_trailing_separator_is_still_the_root()
    {
        var target = await TitleFolderGuard.ForMovieAsync(
            Path.Combine(Root, "Dune.mkv"), [Root + Path.DirectorySeparatorChar], Nobody, default);

        Assert.Equal("flat-layout", target.Code);
    }

    [Fact]
    public async Task A_folder_that_holds_a_library_is_never_renameable()
    {
        var parent = Path.GetDirectoryName(Root)!;
        var target = await TitleFolderGuard.ForMovieAsync(Path.Combine(parent, "Dune.mkv"), Roots, Nobody, default);

        Assert.Equal("library-folder", target.Code);
    }

    [Fact]
    public async Task With_no_library_folders_known_nothing_is_renameable()
    {
        var target = await TitleFolderGuard.ForMovieAsync(Path.Combine(Root, "Dune", "Dune.mkv"), [], Nobody, default);

        Assert.False(target.IsSafe);
        Assert.Equal("outside-library", target.Code);
    }

    [Fact]
    public async Task A_folder_with_another_titles_file_is_not_the_titles_own()
    {
        var target = await TitleFolderGuard.ForMovieAsync(Path.Combine(Root, "Mixed", "Dune.mkv"), Roots, Somebody, default);

        Assert.Equal("shared-folder", target.Code);
    }

    [Fact]
    public async Task A_file_outside_every_library_is_refused()
    {
        var elsewhere = Path.Combine(Path.GetTempPath(), "somewhere-else", "Dune", "Dune.mkv");
        var target = await TitleFolderGuard.ForMovieAsync(elsewhere, Roots, Nobody, default);

        Assert.Equal("outside-library", target.Code);
    }

    [Fact]
    public async Task A_drive_or_filesystem_root_is_never_renameable()
    {
        var driveRoot = Path.GetPathRoot(Path.GetTempPath())!;
        var target = await TitleFolderGuard.ForMovieAsync(Path.Combine(driveRoot, "Dune.mkv"), [], Nobody, default);

        Assert.False(target.IsSafe);
    }

    [Fact]
    public async Task A_show_with_one_season_targets_the_show_folder_not_the_season()
    {
        var show = Path.Combine(Root, "The Wire");
        var target = await TitleFolderGuard.ForSeriesAsync(
            [Path.Combine(show, "Season 01", "e1.mkv"), Path.Combine(show, "Season 01", "e2.mkv")],
            Roots, Nobody, default);

        Assert.Equal(show, target.Folder);
    }

    [Fact]
    public async Task A_show_with_no_known_library_is_refused_rather_than_guessed_at()
    {
        var show = Path.Combine(Path.GetTempPath(), "guard-loose", "The Wire");
        var target = await TitleFolderGuard.ForSeriesAsync(
            [Path.Combine(show, "Season 1", "e1.mkv")], [], Nobody, default);

        Assert.False(target.IsSafe);
        Assert.Equal("outside-library", target.Code);
    }

    [Fact]
    public async Task Episodes_straight_in_the_library_folder_have_no_show_folder()
    {
        var target = await TitleFolderGuard.ForSeriesAsync([Path.Combine(Root, "e1.mkv")], Roots, Nobody, default);

        Assert.Equal("flat-layout", target.Code);
    }

    [Fact]
    public async Task Episodes_spread_over_two_folders_are_refused()
    {
        var target = await TitleFolderGuard.ForSeriesAsync(
            [Path.Combine(Root, "A", "e1.mkv"), Path.Combine(Root, "B", "e2.mkv")], Roots, Nobody, default);

        Assert.Equal("spread-out", target.Code);
    }

    [Theory]
    [InlineData("Season 01", true)]
    [InlineData("Season 1 (2019)", true)]
    [InlineData("S02", true)]
    [InlineData("Specials", true)]
    [InlineData("Series 3", true)]
    [InlineData("The Season Ticket", false)]
    [InlineData("Sintel (2010)", false)]
    public void Season_folder_names_are_recognised(string name, bool expected) =>
        Assert.Equal(expected, TitleFolderGuard.IsSeasonFolderName(name));
}
