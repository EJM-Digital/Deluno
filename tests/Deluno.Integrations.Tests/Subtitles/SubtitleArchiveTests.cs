using System.IO.Compression;
using System.Text;
using Deluno.Integrations.Subtitles;

namespace Deluno.Integrations.Tests.Subtitles;

/// <summary>
/// Getting a subtitle out of whatever a provider sent: a zip is opened (preferring .srt), a plain file is
/// passed through, and an error page or a zip with no subtitle in it is refused.
/// </summary>
public sealed class SubtitleArchiveTests
{
    private static byte[] Zip(params (string Name, string Body)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, body) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(body);
            }
        }

        return stream.ToArray();
    }

    private static string Text(byte[]? data) => Encoding.UTF8.GetString(data!);

    [Fact]
    public void Nothing_in_gives_nothing_out_and_a_plain_file_passes_through_unchanged()
    {
        Assert.Null(SubtitleArchive.Extract([]));
        var srt = Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nHi\n");
        Assert.Same(srt, SubtitleArchive.Extract(srt));
        var shortPk = new byte[] { 0x50, 0x4B, 0x03, 0x04 };
        Assert.Same(shortPk, SubtitleArchive.Extract(shortPk));
    }

    [Fact]
    public void A_zip_gives_up_its_srt_in_preference_to_other_subtitle_formats()
    {
        var zip = Zip(("readme.txt", "hello"), ("film.ass", "[Script Info]"), ("Film.EN.SRT", "the srt"));
        Assert.Equal("the srt", Text(SubtitleArchive.Extract(zip)));
    }

    [Theory]
    [InlineData("film.ass")]
    [InlineData("film.ssa")]
    [InlineData("film.sub")]
    [InlineData("film.vtt")]
    public void A_zip_with_only_another_subtitle_format_gives_that_one(string name)
        => Assert.Equal("body", Text(SubtitleArchive.Extract(Zip(("notes.txt", "x"), (name, "body")))));

    [Fact]
    public void A_zip_with_no_subtitle_in_it_is_refused()
        => Assert.Null(SubtitleArchive.Extract(Zip(("notes.txt", "x"), ("poster.jpg", "y"))));

    [Fact]
    public void Bytes_that_start_like_a_zip_but_are_not_one_are_refused()
    {
        var bogus = new byte[] { 0x50, 0x4B, 0x03, 0x04, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        Assert.Null(SubtitleArchive.Extract(bogus));
    }

    [Theory]
    [InlineData("1\n00:00:01,000 --> 00:00:02,000\nHi", true)]
    [InlineData("﻿  \r\n1\n00:00:01,000 --> 00:00:02,000", true)]
    [InlineData("WEBVTT\n\n00:01.000 --> 00:02.000", true)]
    [InlineData("webvtt - header", true)]
    [InlineData("cue text with --> arrow in it", true)]
    [InlineData("42 some numbered line", true)]
    [InlineData("[Script Info]\nTitle: x", true)]
    [InlineData("[V4 Styles]\nFormat: Name", true)]
    [InlineData("[V4+ Styles]\nFormat: Name", true)]
    [InlineData("[Something]\n[Events]\nDialogue:", true)]
    [InlineData("[{\"error\":\"rate limited\"}]", false)]
    [InlineData("<!DOCTYPE html><html></html>", false)]
    [InlineData("<html><body>sign in</body></html>", false)]
    [InlineData("<?xml version=\"1.0\"?><error/>", false)]
    [InlineData("{\"error\":\"limit reached\"}", false)]
    [InlineData("just words and no timing", false)]
    [InlineData("short", false)]
    public void An_error_page_does_not_read_as_a_subtitle(string content, bool subtitle)
        => Assert.Equal(subtitle, SubtitleArchive.LooksLikeSubtitle(Encoding.UTF8.GetBytes(content)));
}
