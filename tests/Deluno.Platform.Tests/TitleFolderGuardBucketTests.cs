using Deluno.Platform;

namespace Deluno.Platform.Tests;

/// <summary>#1067: the folder comes from the title's own files, never from a bucket folder above them, and the disk is checked too.</summary>
public sealed class TitleFolderGuardBucketTests
{
    private static readonly string[] Roots = ["/tv", "/movies"];

    private static Task<bool> Nobody(string folder, CancellationToken ct) => Task.FromResult(false);
    private static Func<string, IEnumerable<string>> Disk(params string[] files) => _ => files;

    [Fact]
    public async Task A_show_inside_a_category_folder_gets_its_own_folder_not_the_category()
    {
        var target = await TitleFolderGuard.ForSeriesAsync(
            ["/tv/Anime/Bleach/Season 1/e1.mkv", "/tv/Anime/Bleach/Season 2/e1.mkv"], Roots, Nobody, default,
            Disk("/tv/Anime/Bleach/Season 1/e1.mkv", "/tv/Anime/Bleach/Season 2/e1.mkv"));

        Assert.Equal("/tv/Anime/Bleach", target.Folder);
    }

    [Fact]
    public async Task A_show_with_one_season_in_a_category_folder_steps_up_out_of_the_season_only()
    {
        var target = await TitleFolderGuard.ForSeriesAsync(
            ["/tv/Anime/Bleach/S01/e1.mkv"], Roots, Nobody, default, Disk("/tv/Anime/Bleach/S01/e1.mkv"));

        Assert.Equal("/tv/Anime/Bleach", target.Folder);
    }

    [Fact]
    public async Task Episodes_straight_in_a_category_folder_are_refused_as_a_group_folder()
    {
        var target = await TitleFolderGuard.ForSeriesAsync(
            ["/tv/Anime/e1.mkv"], Roots, Nobody, default, Disk("/tv/Anime/e1.mkv"));

        Assert.Equal("bucket-folder", target.Code);
    }

    [Fact]
    public async Task A_season_folder_straight_under_the_library_is_a_flat_layout()
    {
        var target = await TitleFolderGuard.ForSeriesAsync(
            ["/tv/Season 1/e1.mkv"], Roots, Nobody, default, Disk());

        Assert.Equal("flat-layout", target.Code);
    }

    [Theory]
    [InlineData("/movies/A/Dune.mkv")]
    [InlineData("/movies/0-9/2001.mkv")]
    [InlineData("/movies/A-C/Alien.mkv")]
    [InlineData("/movies/Star Wars Collection/A New Hope.mkv")]
    [InlineData("/movies/#/9.mkv")]
    public async Task A_letter_or_collection_folder_is_never_a_movies_own(string file)
    {
        var target = await TitleFolderGuard.ForMovieAsync(file, Roots, Nobody, default, Disk(file));

        Assert.Equal("bucket-folder", target.Code);
    }

    [Fact]
    public async Task A_movie_in_its_own_folder_inside_a_letter_folder_gets_that_folder()
    {
        var target = await TitleFolderGuard.ForMovieAsync(
            "/movies/D/Dune (2021)/Dune.mkv", Roots, Nobody, default,
            Disk("/movies/D/Dune (2021)/Dune.mkv", "/movies/D/Dune (2021)/Dune.en.srt"));

        Assert.Equal("/movies/D/Dune (2021)", target.Folder);
    }

    [Fact]
    public async Task An_untracked_video_file_beside_a_movie_makes_its_folder_shared()
    {
        var target = await TitleFolderGuard.ForMovieAsync(
            "/movies/Dune/Dune.mkv", Roots, Nobody, default,
            Disk("/movies/Dune/Dune.mkv", "/movies/Dune/Other Film.MP4"));

        Assert.Equal("shared-folder", target.Code);
        Assert.Contains("Other Film.MP4", target.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_video_file_in_a_sub_folder_of_a_show_folder_makes_it_shared()
    {
        var target = await TitleFolderGuard.ForSeriesAsync(
            ["/tv/Bleach/Season 1/e1.mkv"], Roots, Nobody, default,
            Disk("/tv/Bleach/Season 1/e1.mkv", "/tv/Bleach/Naruto/Season 1/e1.mkv"));

        Assert.Equal("shared-folder", target.Code);
    }

    [Fact]
    public async Task The_titles_own_files_match_whatever_the_case_or_separators()
    {
        var target = await TitleFolderGuard.ForMovieAsync(
            "/movies/Dune/Dune.mkv", Roots, Nobody, default, Disk("\\movies\\DUNE\\dune.MKV"));

        Assert.True(target.IsSafe);
    }

    [Fact]
    public async Task Roots_and_files_differing_only_in_case_still_work_and_the_disk_spelling_is_returned()
    {
        var target = await TitleFolderGuard.ForSeriesAsync(
            ["/TV/The Wire/Season 1/e1.mkv"], ["/tv/"], Nobody, default, Disk());

        Assert.Equal("/TV/The Wire", target.Folder);
    }

    [Fact]
    public async Task Unc_paths_and_trailing_separators_work_on_every_platform()
    {
        var movie = await TitleFolderGuard.ForMovieAsync(
            @"\\nas\films\Dune (2021)\Dune.mkv", [@"\\nas\films\"], Nobody, default, Disk());
        var show = await TitleFolderGuard.ForSeriesAsync(
            [@"\\nas\tv\Bleach\Season 1\e1.mkv", @"\\nas\tv\Bleach\Season 2\e1.mkv"], [@"\\NAS\TV"], Nobody, default, Disk());
        var flat = await TitleFolderGuard.ForMovieAsync(
            @"\\nas\films\Dune.mkv", [@"\\nas\films\"], Nobody, default, Disk());

        Assert.Equal(@"\\nas\films\Dune (2021)", movie.Folder);
        Assert.Equal(@"\\nas\tv\Bleach", show.Folder);
        Assert.Equal("flat-layout", flat.Code);
    }

    [Fact]
    public async Task A_folder_that_cannot_be_read_is_refused_not_assumed_clean()
    {
        var target = await TitleFolderGuard.ForMovieAsync(
            "/movies/Dune/Dune.mkv", Roots, Nobody, default, _ => throw new UnauthorizedAccessException());

        Assert.Equal("unreadable-folder", target.Code);
    }

    [Fact]
    public async Task The_real_disk_is_looked_at_by_default()
    {
        var library = Path.Combine(Path.GetTempPath(), "guard-disk-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(library, "Dune (2021)");
        Directory.CreateDirectory(Path.Combine(folder, "Extras"));
        var own = Path.Combine(folder, "Dune.mkv");
        await File.WriteAllTextAsync(own, "x");
        try
        {
            var clean = await TitleFolderGuard.ForMovieAsync(own, [library], Nobody, default);
            await File.WriteAllTextAsync(Path.Combine(folder, "Extras", "Untracked.mkv"), "x");
            var dirty = await TitleFolderGuard.ForMovieAsync(own, [library], Nobody, default);

            Assert.True(clean.IsSafe);
            Assert.Equal("shared-folder", dirty.Code);
        }
        finally
        {
            Directory.Delete(library, recursive: true);
        }
    }
}
