using Deluno.Filesystem;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>Which video of a folder is judged, and when a title tag counts as evidence of a different film (#917, #1187, #1520).</summary>
public sealed class DownloadIntegrityCheckSelectionTests
{
    [Theory]
    [InlineData("Some Other Name 2010 1080p")]
    [InlineData("")]
    public void A_film_folder_whose_files_are_none_named_like_the_release_judges_the_largest(string releaseName)
    {
        var judged = DownloadIntegrityCheck.SelectFilesToJudge(["/dl/Feature.mkv", "/dl/Extras.mkv"], releaseName);

        Assert.Equal(["/dl/Feature.mkv"], judged);
    }

    [Fact]
    public void A_film_folder_judges_the_file_named_like_the_release_even_when_it_is_not_the_largest()
    {
        var judged = DownloadIntegrityCheck.SelectFilesToJudge(["/dl/Making.Of.mkv", "/dl/Sintel.2010.mkv"], "Sintel 2010");

        Assert.Equal(["/dl/Sintel.2010.mkv"], judged);
    }

    [Fact]
    public void A_name_too_short_to_compare_is_no_evidence_of_a_different_film()
    {
        Assert.False(DownloadIntegrityCheck.EmbeddedTitleContradicts("Sintel", "It"));
        Assert.False(DownloadIntegrityCheck.EmbeddedTitleContradicts("Sintel", "It", ["Sintel Short"]));
    }

    [Fact]
    public void Another_name_too_short_or_missing_is_skipped_and_the_known_title_still_decides()
    {
        Assert.True(DownloadIntegrityCheck.EmbeddedTitleContradicts("Totally Different Movie", "Big Buck Bunny", ["Ab", ""]));
        Assert.False(DownloadIntegrityCheck.EmbeddedTitleContradicts("Big Buck Bunny", "Big Buck Bunny", ["Ab"]));
        Assert.False(DownloadIntegrityCheck.EmbeddedTitleContradicts("Rabbit Film", "Big Buck Bunny", ["Ab", "Rabbit Film"]));
    }
}
