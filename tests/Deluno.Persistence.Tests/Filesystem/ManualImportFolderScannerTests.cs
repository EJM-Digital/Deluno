using Deluno.Filesystem;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>
/// Manual import scans a folder rather than one file at a time (#783). These
/// tests cover the two pieces of that scan that have no server round-trip to
/// exercise them: guessing a title/year from a release-style file name, and
/// finding every video file under a folder (recursing, so a season pack's
/// per-episode files show up the same way a flat folder of movies does).
/// </summary>
public sealed class ManualImportFolderScannerTests
{
    [Theory]
    [InlineData("Arrival.2016.WEB.1080p-GROUP.mkv", "Arrival", 2016)]
    [InlineData("The.Matrix.1999.BluRay.1080p.x264-GROUP.mkv", "The Matrix", 1999)]
    [InlineData("Some Show S01E02 720p HDTV.mkv", "Some Show", null)]
    public void Guesses_title_and_year_from_a_release_style_name(string fileName, string expectedTitle, int? expectedYear)
    {
        var (title, year) = ManualImportFolderScanner.GuessTitleAndYear(fileName);

        Assert.Equal(expectedTitle, title);
        Assert.Equal(expectedYear, year);
    }

    [Theory]
    [InlineData("S01E02.mkv")]
    [InlineData("1080p.mkv")]
    public void A_name_that_is_all_release_noise_is_flagged_unmatched_rather_than_guessed_as_a_title(string fileName)
    {
        var (title, year) = ManualImportFolderScanner.GuessTitleAndYear(fileName);

        Assert.Null(title);
    }

    [Fact]
    public void Recurses_into_subfolders_so_a_season_packs_episode_files_are_found()
    {
        var root = Directory.CreateTempSubdirectory("manual-import-scan-");
        try
        {
            var seasonFolder = Directory.CreateDirectory(Path.Combine(root.FullName, "Season 01"));
            var moviePath = Path.Combine(root.FullName, "Arrival.2016.WEB.1080p-GROUP.mkv");
            var episodePath = Path.Combine(seasonFolder.FullName, "Show.S01E01.mkv");
            var nonVideoPath = Path.Combine(root.FullName, "Arrival.2016.nfo");
            File.WriteAllText(moviePath, string.Empty);
            File.WriteAllText(episodePath, string.Empty);
            File.WriteAllText(nonVideoPath, string.Empty);

            var found = ManualImportFolderScanner.FindVideoFiles(root.FullName);

            Assert.Contains(moviePath, found);
            Assert.Contains(episodePath, found);
            Assert.DoesNotContain(nonVideoPath, found);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Caps_how_many_files_a_single_scan_returns()
    {
        var root = Directory.CreateTempSubdirectory("manual-import-scan-cap-");
        try
        {
            for (var i = 0; i < 5; i++)
            {
                File.WriteAllText(Path.Combine(root.FullName, $"File.{i}.mkv"), string.Empty);
            }

            var found = ManualImportFolderScanner.FindVideoFiles(root.FullName, take: 3);

            Assert.Equal(3, found.Count);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
