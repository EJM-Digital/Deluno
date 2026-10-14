using System.Text;
using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// A fetched subtitle written beside its video by the real writer, on a real temp folder: under the scanner's own name,
/// moved into place from a .partial file that never stays, replacing an earlier fetch of the same language, and always
/// UTF-8 without a byte-order mark whatever the provider sent.
/// </summary>
public sealed class WritingSubtitlesOnDiskTests
{
    private const string Text = "1\n00:00:01,000 --> 00:00:02,000\nCafé “quoted”\n";

    private static async Task<(ApplicationTestHost App, ISubtitleFileWriter Writer, string Video)> StartAsync()
    {
        var app = await ApplicationTestHost.StartAsync();
        var folder = Path.Combine(app.DataRoot, "movies", "Big Buck Bunny (2008)");
        Directory.CreateDirectory(folder);
        var video = Path.Combine(folder, "Big Buck Bunny (2008).mkv");
        File.WriteAllBytes(video, [1, 2, 3]);
        return (app, app.Services.GetRequiredService<ISubtitleFileWriter>(), video);
    }

    [Theory]
    [InlineData(false, "Big Buck Bunny (2008).en.srt")]
    [InlineData(true, "Big Buck Bunny (2008).en.sdh.srt")]
    public async Task A_subtitle_is_written_beside_its_video_under_the_scanners_own_name_with_no_partial_file_left(bool hearingImpaired, string expectedName)
    {
        var (app, writer, video) = await StartAsync();
        await using var _ = app;

        var written = await writer.WriteAsync(video, "English", hearingImpaired, Encoding.UTF8.GetBytes(Text), CancellationToken.None);

        Assert.Equal(Path.Combine(Path.GetDirectoryName(video)!, expectedName), written);
        Assert.Equal(Text, File.ReadAllText(written));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(video)!, "*.partial"));
    }

    [Fact]
    public async Task A_second_fetch_of_the_same_language_replaces_the_first_instead_of_adding_another()
    {
        var (app, writer, video) = await StartAsync();
        await using var _ = app;

        var first = await writer.WriteAsync(video, "English", false, Encoding.UTF8.GetBytes("first"), CancellationToken.None);
        var second = await writer.WriteAsync(video, "English", false, Encoding.UTF8.GetBytes("second, a better one"), CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal("second, a better one", File.ReadAllText(second));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(video)!, "*.srt"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(video)!, "*.partial"));
    }

    [Theory]
    [InlineData("windows-1252")]
    [InlineData("utf-16")]
    [InlineData("utf-8-with-bom")]
    public async Task Whatever_encoding_the_provider_sent_the_file_on_disk_is_utf8_without_a_byte_order_mark(string sent)
    {
        var (app, writer, video) = await StartAsync();
        await using var _ = app;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = sent switch
        {
            "windows-1252" => Encoding.GetEncoding(1252).GetBytes(Text.Replace("“", "\"").Replace("”", "\"")),
            "utf-16" => Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Text)).ToArray(),
            _ => Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Text)).ToArray()
        };

        var written = await writer.WriteAsync(video, "English", false, bytes, CancellationToken.None);

        var onDisk = File.ReadAllBytes(written);
        Assert.False(onDisk.Length >= 3 && onDisk[0] == 0xEF && onDisk[1] == 0xBB && onDisk[2] == 0xBF, "no byte-order mark");
        Assert.False(onDisk.Length >= 2 && onDisk[0] == 0xFF && onDisk[1] == 0xFE, "not UTF-16");
        var decoded = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(onDisk);
        Assert.Contains("Café", decoded);
    }

    [Fact]
    public async Task A_subtitle_for_a_video_whose_folder_is_gone_is_refused_and_nothing_is_written()
    {
        var (app, writer, video) = await StartAsync();
        await using var _ = app;
        var folder = Path.GetDirectoryName(video)!;
        Directory.Delete(folder, recursive: true);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => writer.WriteAsync(video, "English", false, Encoding.UTF8.GetBytes(Text), CancellationToken.None));

        Assert.False(Directory.Exists(folder), "no orphan folder or subtitle was made for a video that is gone");
    }
}
