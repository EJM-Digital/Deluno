using Deluno.Contracts;

namespace Deluno.Persistence.Tests.Media;

/// <summary>
/// #988: which embedded subtitle codecs are pictures, not text, and so
/// cannot be a duplicate of the words a provider would fetch.
/// </summary>
public sealed class SubtitlePictureCodecsTests
{
    [Theory]
    [InlineData("hdmv_pgs_subtitle")]
    [InlineData("dvd_subtitle")]
    [InlineData("HDMV_PGS_SUBTITLE")]
    [InlineData("  dvd_subtitle  ")]
    public void Recognises_PGS_and_VobSub_by_their_ffprobe_codec_name(string codec)
        => Assert.True(SubtitlePictureCodecs.IsPictureBased(codec));

    [Theory]
    [InlineData("subrip")]
    [InlineData("ass")]
    [InlineData("webvtt")]
    [InlineData("srt")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_text_codec_or_nothing_at_all_is_not_picture_based(string? codec)
        => Assert.False(SubtitlePictureCodecs.IsPictureBased(codec));
}
