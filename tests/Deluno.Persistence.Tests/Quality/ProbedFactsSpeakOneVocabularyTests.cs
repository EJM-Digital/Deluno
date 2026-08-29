using Deluno.Quality;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>
/// A codec read from the file and the same codec read from the name are the
/// same word.
///
/// <para>The release name says <c>x264</c> and ffprobe says <c>h264</c>. If one
/// becomes "H.264" and the other "h264", a filter for one finds half a library
/// and nothing says so — the shape of defect this codebase keeps paying for,
/// and the reason the probe was worth wiring at all: on a renamed library the
/// name carries nothing, so the Codec switch drew a dash on every card.</para>
/// </summary>
public sealed class ProbedFactsSpeakOneVocabularyTests
{
    /// <summary>
    /// ffprobe's word, a release name that means the same thing, and what both
    /// must come out as.
    /// </summary>
    [Theory]
    [InlineData("h264", "Movie.2016.1080p.BluRay.x264-GRP.mkv", "H.264")]
    [InlineData("hevc", "Movie.2016.2160p.WEB.x265-GRP.mkv", "HEVC")]
    [InlineData("av1", "Movie.2016.2160p.WEB.AV1-GRP.mkv", "AV1")]
    [InlineData("vp9", "Movie.2016.1080p.WEB.VP9-GRP.mkv", "VP9")]
    [InlineData("mpeg2video", "Movie.2016.1080p.HDTV.MPEG-2-GRP.mkv", "MPEG-2")]
    public void The_file_and_the_name_agree_about_the_video(string probed, string fileName, string expected)
    {
        Assert.Equal(expected, MediaProbedFacts.VideoCodec(probed));
        Assert.Equal(expected, MediaFileNameFacts.Parse(fileName).VideoCodec);
    }

    [Theory]
    [InlineData("truehd", null, "Movie.2016.1080p.BluRay.TrueHD.5.1-GRP.mkv", "TrueHD")]
    [InlineData("eac3", null, "Movie.2016.1080p.WEB.EAC3.5.1-GRP.mkv", "E-AC-3")]
    [InlineData("ac3", null, "Movie.2016.1080p.WEB.AC3.5.1-GRP.mkv", "AC-3")]
    [InlineData("aac", null, "Movie.2016.1080p.WEB.AAC.2.0-GRP.mkv", "AAC")]
    [InlineData("flac", null, "Movie.2016.1080p.BluRay.FLAC.5.1-GRP.mkv", "FLAC")]
    [InlineData("dts", null, "Movie.2016.1080p.BluRay.DTS.5.1-GRP.mkv", "DTS")]
    public void The_file_and_the_name_agree_about_the_audio(
        string probed, string? profile, string fileName, string expected)
    {
        Assert.Equal(expected, MediaProbedFacts.AudioCodec(probed, profile));
        Assert.Equal(expected, MediaFileNameFacts.Parse(fileName).AudioCodec);
    }

    /// <summary>
    /// The profile is what separates two things ffprobe calls by one name.
    /// </summary>
    [Theory]
    // Both are "dts" to ffprobe, and nobody choosing a file thinks they are the
    // same thing.
    [InlineData("dts", "DTS-HD MA", "DTS-HD")]
    [InlineData("dts", "DTS:X", "DTS:X")]
    [InlineData("dts", null, "DTS")]
    // Atmos rides on TrueHD, and the name parser calls that Atmos too.
    [InlineData("truehd", "Dolby TrueHD + Dolby Atmos", "Atmos")]
    [InlineData("truehd", null, "TrueHD")]
    public void The_profile_separates_what_the_codec_name_cannot(string probed, string? profile, string expected)
        => Assert.Equal(expected, MediaProbedFacts.AudioCodec(probed, profile));

    /// <summary>
    /// A layout is what a person says: 5.1, not "5.1(side)" and not "6".
    /// </summary>
    [Theory]
    [InlineData("5.1(side)", 6, "5.1")]
    [InlineData("5.1", 6, "5.1")]
    [InlineData("7.1", 8, "7.1")]
    [InlineData("stereo", 2, "2.0")]
    [InlineData("mono", 1, "1.0")]
    // No layout stated, so the count answers.
    [InlineData(null, 6, "5.1")]
    [InlineData(null, 8, "7.1")]
    [InlineData(null, 2, "2.0")]
    // Neither: the probe did not say, and inventing a layout would be worse
    // than admitting it.
    [InlineData(null, null, null)]
    [InlineData("", 0, null)]
    public void A_channel_layout_reads_the_way_a_person_writes_it(string? layout, int? channels, string? expected)
        => Assert.Equal(expected, MediaProbedFacts.AudioChannels(layout, channels));

    [Fact]
    public void The_probed_channels_match_what_the_name_parser_produces()
    {
        // The same file described both ways. These have to agree or "5.1" finds
        // the files whose names said so and misses the ones only the probe read.
        Assert.Equal(
            MediaFileNameFacts.Parse("Movie.2016.1080p.BluRay.DTS.5.1-GRP.mkv").AudioChannels,
            MediaProbedFacts.AudioChannels("5.1(side)", 6));
    }

    /// <summary>
    /// Nothing is invented from silence.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unread_stream_says_nothing(string? probed)
    {
        Assert.Null(MediaProbedFacts.VideoCodec(probed));
        Assert.Null(MediaProbedFacts.AudioCodec(probed, null));
    }

    /// <summary>
    /// A codec nobody listed still reads as itself.
    /// </summary>
    [Fact]
    public void A_codec_deluno_has_no_word_for_is_reported_rather_than_dropped()
    {
        // A file in something exotic should say what it is. Returning null
        // would put it in the same bucket as "nobody looked", which is the
        // distinction this whole change exists to keep.
        Assert.Equal("PRORES", MediaProbedFacts.VideoCodec("prores"));
        Assert.Equal("VORBIS", MediaProbedFacts.AudioCodec("vorbis", null));
    }
}
