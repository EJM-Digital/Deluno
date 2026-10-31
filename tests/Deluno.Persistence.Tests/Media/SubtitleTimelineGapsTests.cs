using System.Text;
using Deluno.Filesystem.Subtitles;

namespace Deluno.Persistence.Tests.Media;

/// <summary>Timing lines a subtitle file can get wrong, and the text encodings it can arrive in. #1268.</summary>
public sealed class SubtitleTimelineGapsTests
{
    [Theory]
    [InlineData("00:00:01,000 --> nonsense")]
    [InlineData("nonsense --> 00:00:02,000")]
    [InlineData(" --> 00:00:02,000")]
    [InlineData("00:00:01 --> 00:00:02,000")]
    [InlineData("5,000 --> 00:00:02,000")]
    [InlineData("1:2:3:4,000 --> 00:00:02,000")]
    [InlineData("xx:00:01,000 --> 00:00:02,000")]
    [InlineData("00:xx:01,000 --> 00:00:02,000")]
    [InlineData("00:00:xx,000 --> 00:00:02,000")]
    [InlineData("00:00:01,abc --> 00:00:02,000")]
    [InlineData("not a timing line at all")]
    public void A_timing_line_that_cannot_be_read_is_not_a_cue(string timing)
    {
        var cues = SubtitleTimeline.Parse(Encoding.UTF8.GetBytes($"1\n{timing}\nHello\n"));

        Assert.Empty(cues);
    }

    [Fact]
    public void A_timing_line_with_no_hour_a_full_stop_and_position_data_is_still_read()
    {
        var cues = SubtitleTimeline.Parse(Encoding.UTF8.GetBytes("01:02.5 --> 01:04.250 X1:1 Y1:2\nHello\n"));

        var cue = Assert.Single(cues);
        Assert.Equal(new TimeSpan(0, 0, 1, 2, 500), cue.Start);
        Assert.Equal(new TimeSpan(0, 0, 1, 4, 250), cue.End);
    }

    [Fact]
    public void A_lone_number_that_is_not_followed_by_a_timing_line_is_dialogue()
    {
        var cues = SubtitleTimeline.Parse(Encoding.UTF8.GetBytes("00:00:01,000 --> 00:00:02,000\nCount to\n3\nnow\n\n7"));

        Assert.Equal("Count to\n3\nnow", Assert.Single(cues).Text);
        var spaced = SubtitleTimeline.Parse(Encoding.UTF8.GetBytes("00:00:01,000 --> 00:00:02,000\nA\n   \nB\n"));
        Assert.Equal("A\n   \nB", Assert.Single(spaced).Text);
    }

    [Fact]
    public void Each_byte_order_mark_is_read_and_a_big_endian_file_is_too()
    {
        const string text = "1\n00:00:01,000 --> 00:00:02,000\nHi\n";
        byte[] bigEndian = [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(text)];
        byte[] littleEndian = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(text)];
        byte[] utf8Bom = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text)];

        Assert.Equal("Hi", Assert.Single(SubtitleTimeline.Parse(bigEndian)).Text);
        Assert.Equal("Hi", Assert.Single(SubtitleTimeline.Parse(littleEndian)).Text);
        Assert.Equal("Hi", Assert.Single(SubtitleTimeline.Parse(utf8Bom)).Text);
    }

    [Fact]
    public void A_windows_1252_file_with_an_undefined_byte_shows_a_replacement_character()
    {
        byte[] bytes = [.. Encoding.ASCII.GetBytes("00:00:01,000 --> 00:00:02,000\ncaf"), 0xE9, 0x20, 0x81, 0x93];

        var text = Assert.Single(SubtitleTimeline.Parse(bytes)).Text;

        Assert.Equal("café �“", text);
    }
}
