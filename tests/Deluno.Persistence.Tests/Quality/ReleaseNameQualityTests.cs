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

    // #1505: "UHD" names the disc the encode came from; the resolution token states the picture.
    [Theory]
    [InlineData("The Fall Guy 2024 Extended Cut PROPER UHD BluRay 1080p DD Atmos 5 1 DoVi HDR10 x265-SM737", "Bluray 1080p", 1920, 804, true)]
    [InlineData("The.Fall.Guy.2024.UHD.BluRay.1080p.DD.Atmos.5.1.x265-SM737", "Bluray 1080p", 1920, 800, true)]
    [InlineData("Film.2024.UHD.BluRay.2160p.x265-GRP", "Bluray 2160p", 3840, 1600, true)]
    [InlineData("Film.2024.4K.Remux.2160p.HDR.x265-GRP", "Remux 2160p", 3840, 1600, true)]
    [InlineData("Film.2024.2160p.WEB-DL.DDP5.1.x265-GRP", "WEB 2160p", 1920, 1080, false)]
    [InlineData("Film.2024.4K.WEB-DL.x265-GRP", "WEB 2160p", 1920, 1080, false)]
    [InlineData("Film.2024.4K.WEB-DL.x265-GRP", "WEB 2160p", 3840, 2160, true)]
    public void An_explicit_resolution_beats_a_UHD_or_4K_source_tag(string name, string quality, int width, int height, bool passes)
    {
        Assert.Equal(quality, ReleaseNameQuality.Read(name));
        var claim = ReleaseNameQuality.ClaimedResolution(name);
        Assert.NotNull(claim);
        Assert.Equal(passes, ReleaseNameQuality.MeetsClaim(claim.Value, width, height));
        Assert.Equal(quality.EndsWith("1080p") ? "1080p" : "2160p", ReleaseNameQuality.ResolutionOf(name));
    }

    // A bare "UHD" names the source disc, not the picture: it ranks as 2160p, but the file check
    // has no resolution to hold the file to, so it never throws away a file over it.
    [Fact]
    public void A_bare_UHD_ranks_as_2160p_but_gives_the_file_check_no_claim()
    {
        const string name = "Film.2024.UHD.BluRay.x265-GRP";
        Assert.Equal("Bluray 2160p", ReleaseNameQuality.Read(name));
        Assert.Equal("2160p", ReleaseNameQuality.ResolutionOf(name));
        Assert.Null(ReleaseNameQuality.ClaimedResolution(name));
    }
}
