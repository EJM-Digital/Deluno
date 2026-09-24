using Deluno.Quality;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// #610: release names from the first real search against live trackers (The
/// Kid, 1921, 24 September 2026), and what each is.
/// </summary>
public sealed class ReleaseNameQualityTests
{
    [Theory]
    [InlineData("The Kid 1921 CC 1080p BluRay x264 nikt0", "Bluray 1080p")]
    [InlineData("The Kid 1921 1080p HMAX WEB DL x264 EAC3", "WEB 1080p")]
    [InlineData("The Kid 1921 1080p AMZN WEB DL DDP5 1 H 264 BiOMA", "WEB 1080p")]
    [InlineData("The Kid 1921 BD1080p MemoriadaTV", "Bluray 1080p")]
    [InlineData("Night of the Living Dead (1968) 720p BRRip x264 -YTS", "Bluray 720p")]
    [InlineData("The Kid 1921 BluRay Remux 1080p DTS DUAL", "Remux 1080p")]
    [InlineData("Charlie Chaplin  The Kid (1921) DVDRip x264 AAC MP4  TBI", "DVD")]
    [InlineData("The Kid [1921] 1080p", "HDTV 1080p")]
    [InlineData("Pioneer One S01E01 720p x264 VODO", "HDTV 720p")]
    [InlineData("The Kid (1921) - Featurettes", "Unknown")]
    [InlineData("Cinema 35 - The Kid (1921)", "Unknown")]
    [InlineData("Малыш / The kid (1921) BDRip | Sub", "Unknown")]
    [InlineData("Night.of.the.Living.Dead.1968.2160p.UHD.BluRay.x265-GRP", "Bluray 2160p")]
    [InlineData("Some.Film.2024.HDCAM.x264", "CAM")]
    public void Reads_what_the_name_says_and_no_more(string name, string expected)
        => Assert.Equal(expected, ReleaseNameQuality.Read(name));

    [Fact]
    public void Unknown_is_the_lowest_quality_there_is()
        => Assert.True(LibraryQualityDecider.GetRank(ReleaseNameQuality.Unknown) < LibraryQualityDecider.GetRank("SDTV"));
}
