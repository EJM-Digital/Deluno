using Deluno.Contracts;
using Deluno.Filesystem;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Media;

/// <summary>
/// The name Deluno writes has to be the name Deluno reads.
///
/// <para><b>This is the guard that stops a silent, unbounded cost.</b> The
/// fetcher writes <c>Dune.en.srt</c>; the scan reads the folder and works out
/// what is already held. If those two disagreed about the shape of a filename by
/// one character, every fetched subtitle would look missing on the next cycle,
/// Deluno would fetch it again, and it would do that for ever — spending
/// somebody's daily OpenSubtitles allowance on a file already sitting on their
/// disk. Nothing on screen would look wrong: the bar would even be green,
/// because the row in the store is written at fetch time.</para>
///
/// <para>Two rules written in two places, held together by one test — the same
/// arrangement V0016's trigger and <c>CatalogueWantedState.Join</c> have, and
/// for the same reason.</para>
/// </summary>
public sealed class SubtitleWriteReadRoundTripTests
{
    [Theory]
    // The library setting stores "en"; ffprobe emits "eng"; a person types
    // "English". All three have to come back as one language and one filename.
    [InlineData("en", false)]
    [InlineData("eng", false)]
    [InlineData("English", false)]
    [InlineData("fr", false)]
    [InlineData("pt-BR", false)]
    [InlineData("en", true)]
    [InlineData("ja", true)]
    public async Task What_the_fetcher_writes_is_what_the_scan_reads_back(string language, bool hearingImpaired)
    {
        using var folder = new TemporaryFolder();
        var video = Path.Combine(folder.Path, "Dune (2021) [Remux-2160p].mkv");
        await File.WriteAllTextAsync(video, "not really a film");

        var writer = new SubtitleFileWriter(NullLogger<SubtitleFileWriter>.Instance);
        var written = await writer.WriteAsync(
            video,
            language,
            hearingImpaired,
            "1\r\n00:00:01,000 --> 00:00:03,000\r\nThe subtitle.\r\n"u8.ToArray(),
            CancellationToken.None);

        var inventory = await new SubtitleInventoryService(new NoProbe()).InspectAsync(video, CancellationToken.None);

        var found = Assert.Single(inventory.Subtitles);
        Assert.Equal(SubtitleLanguages.Normalize(language), found.Language);
        Assert.Equal(hearingImpaired, found.HearingImpaired);
        // A fetched subtitle is never forced. If the reader thought it was, it
        // would not count towards coverage and the fetch would repeat.
        Assert.False(found.Forced);
        Assert.Equal(written, found.Path);
    }

    [Fact]
    public async Task A_second_fetch_of_the_same_language_replaces_rather_than_multiplies()
    {
        using var folder = new TemporaryFolder();
        var video = Path.Combine(folder.Path, "Dune.mkv");
        await File.WriteAllTextAsync(video, "not really a film");

        var writer = new SubtitleFileWriter(NullLogger<SubtitleFileWriter>.Instance);
        await writer.WriteAsync(video, "en", false, "first"u8.ToArray(), CancellationToken.None);
        await writer.WriteAsync(video, "en", false, "second"u8.ToArray(), CancellationToken.None);

        // An upgrade replaces the file rather than leaving Dune.en.srt beside
        // Dune.en.1.srt for a player to choose between.
        Assert.Single(Directory.GetFiles(folder.Path, "*.srt"));
        Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(folder.Path, "Dune.en.srt")));
    }

    [Fact]
    public async Task Nothing_partial_is_left_behind()
    {
        using var folder = new TemporaryFolder();
        var video = Path.Combine(folder.Path, "Dune.mkv");
        await File.WriteAllTextAsync(video, "not really a film");

        await new SubtitleFileWriter(NullLogger<SubtitleFileWriter>.Instance)
            .WriteAsync(video, "en", false, "subtitle"u8.ToArray(), CancellationToken.None);

        // The write goes to `.partial` and is moved, because a half-written
        // `.srt` is a file a player opens and shows nothing from — and the scan
        // would count it as held.
        Assert.Empty(Directory.GetFiles(folder.Path, "*.partial"));
    }

    /// <summary>
    /// No ffprobe, so only the files beside the video are read — which is the
    /// half this test is about, and the half that exists on an install without
    /// ffprobe.
    /// </summary>
    private sealed class NoProbe : IMediaProbeService
    {
        public Task<MediaProbeInfo> ProbeAsync(string filePath, CancellationToken cancellationToken)
            => Task.FromResult(new MediaProbeInfo("unavailable", "ffprobe", null, null, null, null, [], [], []));
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("deluno-subtitles").FullName;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { /* a test folder */ }
        }
    }
}
