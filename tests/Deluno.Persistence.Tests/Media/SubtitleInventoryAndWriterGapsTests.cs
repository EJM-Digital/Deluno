using System.Text;
using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Media;

/// <summary>The edges of reading and writing subtitle files that the main tests walk past. #1268.</summary>
public sealed class SubtitleInventoryAndWriterGapsTests
{
    [Fact]
    public async Task Embedded_tracks_are_read_by_flag_and_by_what_their_title_says()
    {
        using var folder = new TemporaryFolder();
        var video = Path.Combine(folder.Path, "Dune.mkv");
        await File.WriteAllTextAsync(video, "not a film");
        var probe = new MediaProbeInfo("succeeded", "ffprobe", null, 5000, "matroska", null, [], [],
        [
            new MediaSubtitleStreamInfo(1, "subrip", "eng", Forced: true),
            new MediaSubtitleStreamInfo(2, "subrip", "fre", HearingImpaired: true),
            new MediaSubtitleStreamInfo(3, "subrip", "ger", Title: "Foreign parts forced"),
            new MediaSubtitleStreamInfo(4, "subrip", "spa", Title: "English SDH"),
            new MediaSubtitleStreamInfo(5, "subrip", "ita", Title: "Hearing impaired"),
            new MediaSubtitleStreamInfo(6, "subrip", "jpn", Title: "Full"),
            new MediaSubtitleStreamInfo(7, "subrip", "jpn", Title: "Full again")
        ]);

        var inventory = await new SubtitleInventoryService(new StubProbe(probe)).InspectAsync(video, true, CancellationToken.None);

        var byLanguage = inventory.Subtitles.ToDictionary(s => s.Language);
        Assert.True(byLanguage["en"].Forced);
        Assert.True(byLanguage["fr"].HearingImpaired);
        Assert.True(byLanguage["de"].Forced);
        Assert.True(byLanguage["es"].HearingImpaired);
        Assert.True(byLanguage["it"].HearingImpaired);
        Assert.False(byLanguage["ja"].Forced || byLanguage["ja"].HearingImpaired);
        Assert.Equal(6, inventory.Subtitles.Count);
        Assert.Equal(6, byLanguage["ja"].StreamIndex);
    }

    [Fact]
    public async Task A_probe_that_found_no_video_or_audio_gives_empty_facts()
    {
        using var folder = new TemporaryFolder();
        var video = Path.Combine(folder.Path, "Dune.mkv");
        await File.WriteAllTextAsync(video, "not a film");
        var probe = new MediaProbeInfo("succeeded", "ffprobe", null, 5000, "matroska", null, [], [], []);
        var withStreams = new MediaProbeInfo("succeeded", "ffprobe", null, 5000, "matroska", null,
            [new MediaVideoStreamInfo(0, "hevc", "Main 10", 1920, 1080, "yuv420p10le", 24, 1000, "eng")],
            [new MediaAudioStreamInfo(1, "eac3", null, 6, "5.1(side)", 48000, 640, "eng")], []);

        var inventory = await new SubtitleInventoryService(new StubProbe(probe)).InspectAsync(video, true, CancellationToken.None);

        Assert.NotNull(inventory.Probed);
        Assert.Null(inventory.Probed!.VideoCodec);
        Assert.Null(inventory.Probed.AudioCodec);

        var full = await new SubtitleInventoryService(new StubProbe(withStreams)).InspectAsync(video, true, CancellationToken.None);
        Assert.NotNull(full.Probed!.VideoCodec);
        Assert.NotNull(full.Probed.AudioCodec);
    }

    [Fact]
    public void A_name_part_made_only_of_separators_is_not_a_recognised_tag()
    {
        Assert.False(SubtitleInventoryService.AreAllTagsRecognised("-"));
        Assert.True(SubtitleInventoryService.AreAllTagsRecognised("en.forced"));
        Assert.True(SubtitleInventoryService.AreAllTagsRecognised("forced-hi"));
        Assert.True(SubtitleInventoryService.AreAllTagsRecognised("default"));
        Assert.False(SubtitleInventoryService.AreAllTagsRecognised("und"));
        Assert.False(SubtitleInventoryService.AreAllTagsRecognised("forced-xyz"));
    }

    [Fact]
    public async Task An_empty_subtitle_is_written_as_an_empty_file()
    {
        using var folder = new TemporaryFolder();
        var video = Path.Combine(folder.Path, "Dune.mkv");
        await File.WriteAllTextAsync(video, "not a film");
        var writer = new SubtitleFileWriter(NullLogger<SubtitleFileWriter>.Instance, FakePlatformSettingsRepository.With());

        var written = await writer.WriteAsync(video, "en", false, [], CancellationToken.None);

        Assert.Empty(await File.ReadAllBytesAsync(written));
    }

    [Fact]
    public void A_subtitle_is_named_after_its_video_with_its_language_and_variant()
    {
        Assert.Equal("Dune.en.srt", SubtitleFileNaming.For("/m/Dune.mkv", "eng", false));
        Assert.Equal("Dune.und.srt", SubtitleFileNaming.For("/m/Dune.mkv", "nonsense", false));
        Assert.Equal("Dune.sdh.srt", SubtitleFileNaming.For("/m/Dune.mkv", "en", true, omitLanguageCode: true));
        Assert.Equal("Dune.en.forced.srt", SubtitleFileNaming.For("/m/Dune.mkv", "en", true, forced: true));
        Assert.Equal("Dune.srt", SubtitleFileNaming.For("/m/Dune.mkv", "en", false, omitLanguageCode: true));
    }

    [Fact]
    public async Task The_files_beside_a_video_are_listed_by_path()
    {
        using var folder = new TemporaryFolder();
        var video = Path.Combine(folder.Path, "Dune.mkv");
        await File.WriteAllTextAsync(video, "not a film");
        var sidecar = Path.Combine(folder.Path, "Dune.en.srt");
        await File.WriteAllTextAsync(sidecar, "1\n");

        Assert.Equal([sidecar], SubtitleInventoryService.FindSidecarSubtitlePaths(video));
    }

    private sealed class StubProbe(MediaProbeInfo probe) : IMediaProbeService
    {
        public Task<MediaProbeInfo> ProbeAsync(string path, CancellationToken cancellationToken) => Task.FromResult(probe);
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("deluno-subgaps-").FullName;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
