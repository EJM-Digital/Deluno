using Deluno.Quality;

namespace Deluno.Movies.Tests;

/// <summary>
/// Reading what a release name says about the file.
///
/// The cases here are real naming conventions rather than invented ones,
/// because the whole value of parsing instead of probing is that the convention
/// holds — and the whole risk is reading a year or a resolution as something it
/// is not.
/// </summary>
public sealed class MediaFileNameFactsTests
{
    [Theory]
    [InlineData("Arrival.2016.1080p.BluRay.x264-SPARKS", "H.264")]
    [InlineData("Arrival.2016.2160p.UHD.BluRay.x265.HDR-TERMiNAL", "HEVC")]
    [InlineData("Arrival.2016.1080p.WEB-DL.H.264-GROUP", "H.264")]
    [InlineData("Arrival.2016.1080p.WEB-DL.h265-GROUP", "HEVC")]
    [InlineData("Arrival.2016.WEBRip.AV1-GROUP", "AV1")]
    [InlineData("Old.Film.1998.DVDRip.XviD-CLASSIC", "XviD")]
    [InlineData("Arrival.2016.1080p.BluRay-GROUP", null)]
    public void Video_codec_is_read_from_the_name(string name, string? expected)
        => Assert.Equal(expected, MediaFileNameFacts.Parse(name).VideoCodec);

    [Theory]
    [InlineData("Arrival.2016.1080p.BluRay.x264.DTS-HD.MA.5.1-SPARKS", "DTS-HD")]
    [InlineData("Arrival.2016.2160p.TrueHD.Atmos.7.1-GROUP", "TrueHD")]
    [InlineData("Arrival.2016.1080p.WEB-DL.DDP5.1-GROUP", "E-AC-3")]
    [InlineData("Arrival.2016.1080p.WEB-DL.AAC2.0-GROUP", "AAC")]
    [InlineData("Arrival.2016.1080p.BluRay.x264-GROUP", null)]
    public void Audio_codec_is_read_from_the_name(string name, string? expected)
        => Assert.Equal(expected, MediaFileNameFacts.Parse(name).AudioCodec);

    [Theory]
    [InlineData("Arrival.2016.1080p.BluRay.x264.DTS-HD.MA.5.1-SPARKS", "5.1")]
    [InlineData("Arrival.2016.2160p.TrueHD.Atmos.7.1-GROUP", "7.1")]
    [InlineData("Arrival.2016.1080p.WEB-DL.AAC2.0-GROUP", "2.0")]
    public void Audio_channels_are_read_from_the_name(string name, string expected)
        => Assert.Equal(expected, MediaFileNameFacts.Parse(name).AudioChannels);

    [Theory]
    // A resolution ends in a digit and a letter, a year is four digits: neither
    // is a channel layout, and reading them as one is the obvious way to get
    // this wrong.
    [InlineData("Arrival.2016.1080p.BluRay.x264-SPARKS")]
    [InlineData("Movie.2020.720p.WEB-GROUP")]
    [InlineData("Some.Film.2160p-GROUP")]
    public void A_resolution_or_a_year_is_not_an_audio_layout(string name)
        => Assert.Null(MediaFileNameFacts.Parse(name).AudioChannels);

    [Theory]
    [InlineData("Arrival.2016.1080p.BluRay.x264-SPARKS", "SPARKS")]
    [InlineData("Arrival.2016.1080p.WEB-DL.DDP5.1-NTb", "NTb")]
    [InlineData("Arrival.2016.1080p.BluRay.x264-D-Z0N3", "Z0N3")]
    public void Release_group_is_the_trailing_token(string name, string expected)
        => Assert.Equal(expected, MediaFileNameFacts.Parse(name).ReleaseGroup);

    [Theory]
    // Everything here ends in a hyphenated token that is not a group.
    [InlineData("Arrival.2016.1080p.WEB-DL")]
    [InlineData("Arrival.2016.1080p-BluRay")]
    [InlineData("Arrival.2016-1080p")]
    [InlineData("Arrival 2016")]
    public void Qualities_and_containers_are_not_release_groups(string name)
        => Assert.Null(MediaFileNameFacts.Parse(name).ReleaseGroup);

    [Fact]
    public void A_full_path_is_read_by_its_file_name()
    {
        var facts = MediaFileNameFacts.Parse(@"D:\Media\Movies\Arrival (2016)\Arrival.2016.1080p.BluRay.x264.DTS-HD.MA.5.1-SPARKS.mkv");

        Assert.Equal("H.264", facts.VideoCodec);
        Assert.Equal("DTS-HD", facts.AudioCodec);
        Assert.Equal("5.1", facts.AudioChannels);
        Assert.Equal("SPARKS", facts.ReleaseGroup);
    }

    [Theory]
    [InlineData("Movie.2026.FRENCH.1080p.WEB-DL-GRP", new[] { "French" })]
    [InlineData("Movie.2026.MULTI.GERMAN.1080p.WEB-DL-GRP", new[] { "Multi", "German" })]
    [InlineData("Movie.2026.VOSTFR.1080p.WEB-DL-GRP", new[] { "French" })]
    [InlineData("Movie.2026.1080p.WEB-DL-GRP", new string[0])]
    // A language word fused into an unrelated token is not a language tag.
    [InlineData("LeFrenchKiss.2026.1080p.WEB-DL-GRP", new string[0])]
    public void Language_tags_are_read_as_their_own_token(string name, string[] expected)
        => Assert.Equal(expected, MediaFileNameFacts.Parse(name).Languages);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_in_yields_nothing_out(string? name)
        => Assert.Equal(MediaFileFacts.Empty, MediaFileNameFacts.Parse(name));

    [Fact]
    public void A_name_that_says_nothing_still_parses()
    {
        var facts = MediaFileNameFacts.Parse("Some Film (1994).mkv");

        Assert.Null(facts.VideoCodec);
        Assert.Null(facts.AudioCodec);
        Assert.Null(facts.AudioChannels);
        Assert.Null(facts.ReleaseGroup);
    }

    /// <summary>#760: MediaInfo dynamic-range token, ordered most-specific first so HDR10+ and Dolby Vision are not read as plain HDR.</summary>
    [Theory]
    [InlineData("Arrival.2016.2160p.UHD.BluRay.x265.HDR10Plus-GROUP", "HDR10+")]
    [InlineData("Arrival.2016.2160p.UHD.BluRay.x265.DV-GROUP", "Dolby Vision")]
    [InlineData("Arrival.2016.2160p.UHD.BluRay.x265.HDR10-GROUP", "HDR10")]
    [InlineData("Arrival.2016.2160p.UHD.BluRay.x265.HDR-GROUP", "HDR")]
    [InlineData("Arrival.2016.1080p.BluRay.x264-GROUP", null)]
    public void Dynamic_range_is_read_from_the_name(string name, string? expected)
        => Assert.Equal(expected, MediaFileNameFacts.Parse(name).DynamicRange);

    /// <summary>#760: MediaInfo bit-depth token.</summary>
    [Theory]
    [InlineData("Arrival.2016.2160p.UHD.BluRay.x265.10bit-GROUP", "10-bit")]
    [InlineData("Arrival.2016.2160p.UHD.BluRay.x265.8bit-GROUP", "8-bit")]
    [InlineData("Arrival.2016.1080p.BluRay.x264-GROUP", null)]
    public void Bit_depth_is_read_from_the_name(string name, string? expected)
        => Assert.Equal(expected, MediaFileNameFacts.Parse(name).BitDepth);

    [Fact]
    public void Bitrate_is_derived_from_size_and_runtime_or_not_at_all()
    {
        // 4 GB over 90 minutes is about 6.4 Mbps.
        Assert.Equal(6.36, MediaFileNameFacts.Parse("x").VideoCodec is null
            ? MediaFileFacts.ApproximateBitrateMbps(4L * 1024 * 1024 * 1024, 90)
            : null);

        // A guess is worse than nothing: without both inputs there is no answer.
        Assert.Null(MediaFileFacts.ApproximateBitrateMbps(null, 90));
        Assert.Null(MediaFileFacts.ApproximateBitrateMbps(4_000_000_000, null));
        Assert.Null(MediaFileFacts.ApproximateBitrateMbps(0, 90));
        Assert.Null(MediaFileFacts.ApproximateBitrateMbps(4_000_000_000, 0));
    }

    [Theory]
    [InlineData(".mkv")]
    [InlineData(@"C:\Films\.mp4")]
    [InlineData("/films/")]
    public void A_name_with_nothing_left_after_the_folder_and_extension_are_removed_yields_no_facts(string name)
        => Assert.Equal(MediaFileFacts.Empty, MediaFileNameFacts.Parse(name));

    [Theory]
    [InlineData("Film.2016.1080p.Mono.1.0-GROUP", "1.0")]
    [InlineData("Film.2016.1080p.Stereo.2.1-GROUP", "2.1")]
    [InlineData("Film.2016.1080p.AAC.5.2-GROUP", "5.2")]
    [InlineData("Film.2016.1080p.AAC.6.1-GROUP", "6.1")]
    [InlineData("Film.2016.1080p.AAC.9.1-GROUP", "9.1")]
    public void Every_channel_layout_digit_and_subwoofer_digit_is_read(string name, string expected)
        => Assert.Equal(expected, MediaFileNameFacts.Parse(name).AudioChannels);

    [Theory]
    [InlineData("Film.2016.1080p.AAC.5.3-GROUP")]
    [InlineData("Film.2016.1080p.AAC.4.1-GROUP")]
    [InlineData("Film.2016.1080p.AAC.3.0-GROUP")]
    [InlineData("Film.2016.1080p.AAC.8.1-GROUP")]
    public void A_pair_that_is_not_a_channel_layout_is_not_read_as_one(string name)
        => Assert.Null(MediaFileNameFacts.Parse(name).AudioChannels);
}
