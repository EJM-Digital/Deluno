using Deluno.Platform;

namespace Deluno.Platform.Tests;

/// <summary>A title with no file on record has no folder to rename, and says so with the same code for a film and for a show.</summary>
public sealed class TitleFolderGuardNoFilesTests
{
    private static readonly string[] Roots = [Path.Combine(Path.GetTempPath(), "guard-nofiles", "Films")];

    private static Task<bool> Nobody(string folder, CancellationToken ct) => Task.FromResult(false);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_film_with_no_file_on_record_is_refused_with_no_files(string? filePath)
    {
        var target = await TitleFolderGuard.ForMovieAsync(filePath, Roots, Nobody, default);

        Assert.False(target.IsSafe);
        Assert.Equal("no-files", target.Code);
        Assert.Equal("There is no file on record for this title yet, so there is no folder to rename.", target.Message);
    }

    [Fact]
    public async Task A_show_with_no_episode_file_on_record_is_refused_with_no_files()
    {
        var target = await TitleFolderGuard.ForSeriesAsync([], Roots, Nobody, default);

        Assert.False(target.IsSafe);
        Assert.Equal("no-files", target.Code);
        Assert.Equal("There is no file on record for this title yet, so there is no folder to rename.", target.Message);
    }

    [Fact]
    public async Task A_show_whose_files_share_no_folder_at_all_has_no_folder_of_its_own_to_rename()
    {
        var target = await TitleFolderGuard.ForSeriesAsync([@"D:\Shows\Alpha\S01E01.mkv", @"E:\Other\Beta\S01E02.mkv"], Roots, Nobody, default);

        Assert.False(target.IsSafe);
        Assert.Equal("no-files", target.Code);
    }

    [Fact]
    public async Task A_film_filed_straight_in_the_top_folder_of_a_drive_is_refused_and_never_renamed()
    {
        var target = await TitleFolderGuard.ForMovieAsync("/Heat.mkv", Roots, Nobody, default);

        Assert.False(target.IsSafe);
        Assert.NotEqual("no-files", target.Code);
    }
}
