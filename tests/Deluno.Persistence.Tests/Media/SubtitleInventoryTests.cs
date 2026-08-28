using Deluno.Contracts;
using Deluno.Filesystem;

namespace Deluno.Persistence.Tests.Media;

/// <summary>
/// Finding the subtitles a file already has.
///
/// These are written against real files in a real folder because that is the
/// only thing the reader actually looks at, and because every one of these
/// naming conventions came from somewhere: an old Bazarr, a release, a person.
/// </summary>
public sealed class SubtitleInventoryTests
{
    [Fact]
    public void The_three_ways_a_language_is_written_are_one_language()
    {
        // The library setting stores this.
        Assert.Equal("en", SubtitleLanguages.Normalize("en"));
        // ffprobe reports this.
        Assert.Equal("en", SubtitleLanguages.Normalize("eng"));
        // A file beside the video is named this.
        Assert.Equal("en", SubtitleLanguages.Normalize("English"));

        // Two ISO 639-2 codes for one language, which is a real thing and the
        // reason a hand-rolled comparison would have missed half of French.
        Assert.Equal("fr", SubtitleLanguages.Normalize("fre"));
        Assert.Equal("fr", SubtitleLanguages.Normalize("fra"));

        // A locale says which flavour, not which language.
        Assert.Equal("pt", SubtitleLanguages.Normalize("pt-BR"));
        Assert.Equal("zh", SubtitleLanguages.Normalize("zh_Hans"));
    }

    [Fact]
    public void Something_that_is_not_a_language_is_not_guessed_at()
    {
        // Null rather than "und", so a filename's tags can be told apart: only
        // one of "en" and "forced" is a language.
        Assert.Null(SubtitleLanguages.Normalize("forced"));
        Assert.Null(SubtitleLanguages.Normalize("1080p"));
        Assert.Null(SubtitleLanguages.Normalize(""));

        // A language that says it does not know is a fact, and it is kept.
        Assert.Equal("und", SubtitleLanguages.Normalize("und"));
    }

    [Fact]
    public async Task Subtitle_files_beside_the_video_are_found_and_read()
    {
        using var folder = new TempFolder();
        var video = folder.WriteVideo("Big Buck Bunny (2008).mkv");
        folder.Write("Big Buck Bunny (2008).en.srt");
        folder.Write("Big Buck Bunny (2008).spa.srt");
        folder.Write("Big Buck Bunny (2008).en.forced.srt");
        folder.Write("Big Buck Bunny (2008).fr.sdh.srt");
        // Not a subtitle, and not this video's.
        folder.Write("Big Buck Bunny (2008)-thumb.jpg");

        var inventory = await Inspect(video, probe: MediaProbeInfoFake.Unavailable);

        Assert.Equal(
            ["en", "en", "es", "fr"],
            inventory.Subtitles.Select(subtitle => subtitle.Language).Order(StringComparer.Ordinal));

        var forced = Assert.Single(inventory.Subtitles, subtitle => subtitle.Forced);
        Assert.Equal("en", forced.Language);

        var sdh = Assert.Single(inventory.Subtitles, subtitle => subtitle.HearingImpaired);
        Assert.Equal("fr", sdh.Language);

        Assert.All(inventory.Subtitles, subtitle => Assert.Equal(SubtitleSources.External, subtitle.Source));
    }

    [Fact]
    public async Task A_subtitle_with_no_language_in_its_name_is_recorded_as_unknown_rather_than_assumed()
    {
        using var folder = new TempFolder();
        var video = folder.WriteVideo("Arrival (2016).mkv");
        folder.Write("Arrival (2016).srt");

        var inventory = await Inspect(video, probe: MediaProbeInfoFake.Unavailable);

        // Reading this as the library's first wanted language would be right
        // most of the time, and when it was wrong it would stop Deluno fetching
        // a language somebody asked for without ever saying so.
        var subtitle = Assert.Single(inventory.Subtitles);
        Assert.Equal(SubtitleLanguages.Unknown, subtitle.Language);
    }

    [Fact]
    public async Task A_lone_subtitle_next_to_a_lone_video_belongs_to_it_even_when_the_names_differ()
    {
        using var folder = new TempFolder();
        var video = folder.WriteVideo("Arrival (2016).mkv");
        folder.Write("Arrival.2016.1080p.BluRay-SPARKS.eng.srt");

        var inventory = await Inspect(video, probe: MediaProbeInfoFake.Unavailable);

        var subtitle = Assert.Single(inventory.Subtitles);
        Assert.Equal("en", subtitle.Language);
    }

    [Fact]
    public async Task A_Subs_folder_is_read_because_that_is_where_half_of_them_live()
    {
        using var folder = new TempFolder();
        var video = folder.WriteVideo("Dune (2021).mkv");
        folder.WriteNested("Subs", "Dune (2021).de.srt");

        var inventory = await Inspect(video, probe: MediaProbeInfoFake.Unavailable);

        var subtitle = Assert.Single(inventory.Subtitles);
        Assert.Equal("de", subtitle.Language);
    }

    [Fact]
    public async Task Tracks_inside_the_container_count_and_a_forced_one_is_marked_as_forced()
    {
        using var folder = new TempFolder();
        var video = folder.WriteVideo("Dune (2021).mkv");

        var inventory = await Inspect(video, probe: MediaProbeInfoFake.WithSubtitles(
            new MediaSubtitleStreamInfo(2, "subrip", "eng"),
            new MediaSubtitleStreamInfo(3, "subrip", "eng", Forced: true),
            new MediaSubtitleStreamInfo(4, "hdmv_pgs_subtitle", "jpn")));

        Assert.Equal(3, inventory.Subtitles.Count);
        Assert.All(inventory.Subtitles, subtitle => Assert.Equal(SubtitleSources.Embedded, subtitle.Source));
        Assert.Contains(inventory.Subtitles, subtitle => subtitle.Language == "ja");
        Assert.Single(inventory.Subtitles, subtitle => subtitle.Forced);
    }

    [Fact]
    public async Task One_language_held_two_ways_is_still_one_language_and_the_file_wins()
    {
        using var folder = new TempFolder();
        var video = folder.WriteVideo("Dune (2021).mkv");
        folder.Write("Dune (2021).en.srt");

        var inventory = await Inspect(video, probe: MediaProbeInfoFake.WithSubtitles(
            new MediaSubtitleStreamInfo(2, "subrip", "eng")));

        // Counting it twice would let the bar under a poster read past what was
        // asked for.
        var subtitle = Assert.Single(inventory.Subtitles);
        Assert.Equal("en", subtitle.Language);
        Assert.Equal(SubtitleSources.External, subtitle.Source);
    }

    [Fact]
    public async Task Without_ffprobe_the_files_beside_the_video_are_still_read_and_the_gap_is_reported()
    {
        using var folder = new TempFolder();
        var video = folder.WriteVideo("Dune (2021).mkv");
        folder.Write("Dune (2021).en.srt");

        var inventory = await Inspect(video, probe: MediaProbeInfoFake.Unavailable);

        // "No embedded tracks" and "nobody looked" are different facts, and an
        // install with no FFmpeg produces the second one for every file it owns.
        Assert.Equal("unavailable", inventory.ProbeStatus);
        Assert.Single(inventory.Subtitles);
    }

    private static Task<SubtitleInventory> Inspect(string videoPath, MediaProbeInfo probe)
        => new SubtitleInventoryService(new StubMediaProbeService(probe)).InspectAsync(videoPath, probeContainer: true, CancellationToken.None);

    private sealed class StubMediaProbeService(MediaProbeInfo probe) : IMediaProbeService
    {
        public Task<MediaProbeInfo> ProbeAsync(string path, CancellationToken cancellationToken)
            => Task.FromResult(probe);
    }

    private static class MediaProbeInfoFake
    {
        public static MediaProbeInfo Unavailable { get; } =
            new("unavailable", "ffprobe", "ffprobe was not found.", null, null, null, [], [], []);

        public static MediaProbeInfo WithSubtitles(params MediaSubtitleStreamInfo[] subtitles)
            => new("succeeded", "ffprobe", null, 5000, "matroska", null, [], [], subtitles);
    }

    private sealed class TempFolder : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("deluno-subs-").FullName;

        public string WriteVideo(string name)
        {
            var path = Path.Combine(_root, name);
            File.WriteAllText(path, "not really a video");
            return path;
        }

        public void Write(string name) => File.WriteAllText(Path.Combine(_root, name), "1\n00:00:01,000 --> 00:00:02,000\nhello\n");

        public void WriteNested(string folder, string name)
        {
            var nested = Directory.CreateDirectory(Path.Combine(_root, folder)).FullName;
            File.WriteAllText(Path.Combine(nested, name), "1\n00:00:01,000 --> 00:00:02,000\nhello\n");
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A temp folder that will not delete is not a test failure.
            }
        }
    }
}
