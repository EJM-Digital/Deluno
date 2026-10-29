using System.Net;
using System.Net.Http.Json;
using Deluno.Filesystem;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Worker.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// An outside app swaps a library file for another one and says so. On a real host and real files, the film is re-read by the
/// probe job that the notice queued, and the codec, audio format and channel layout Deluno holds really change to what the
/// swapped file contains.
/// </summary>
public sealed class ASwappedFileIsReReadAfterAnOutsideAppSaysSoOnDiskTests
{
    private static MediaProbeInfo Read(string video, string audio, int channels, string layout) => new(
        Status: "succeeded", Tool: "test", Message: null, DurationSeconds: 7200, Container: "matroska", Bitrate: 12_000_000,
        VideoStreams: [new MediaVideoStreamInfo(0, video, null, 1920, 1080, null, 23.976, null, "eng")],
        AudioStreams: [new MediaAudioStreamInfo(1, audio, null, channels, layout, null, null, "eng")],
        SubtitleStreams: []);

    private static JobQueueItem Job() => new(
        "job-1", "library.media.probe", "external", "running", null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        null, null, null, null, "movie", null, null, "media-probe:movie", 3, null, null);

    [Fact]
    public async Task The_codec_audio_and_channels_Deluno_holds_change_to_what_the_swapped_file_contains()
    {
        var current = Read("h264", "aac", 2, "stereo");
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: RealDiskLibrary.UseProbe(new ScriptedProbe(_ => current)));
        var disk = await RealDiskLibrary.OpenAsync(app);
        var folder = Directory.CreateDirectory(Path.Combine(disk.Root, "Swapped Film (2019)")).FullName;
        var path = Path.Combine(folder, "Swapped Film (2019) - 1080p.mkv");
        await File.WriteAllBytesAsync(path, new byte[2048]);
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        await movies.ImportExistingAsync(
            disk.Library.Id, "Swapped Film", 2019,
            wantedStatus: "covered", wantedReason: "Imported", currentQuality: "WEB 1080p", targetQuality: "WEB 1080p",
            qualityCutoffMet: true, filePath: path, fileSizeBytes: 2048, cancellationToken: CancellationToken.None);
        var movieId = (await movies.ListAsync(CancellationToken.None)).Single().Id;
        var probe = app.Services.GetRequiredService<JobHandlerRegistry>().Resolve("library.media.probe");

        await probe.HandleAsync(Job(), CancellationToken.None);
        Assert.Equal("H.264", await FactAsync(app, movieId, "video_codec"));
        Assert.Equal("AAC", await FactAsync(app, movieId, "audio_codec"));
        Assert.Equal("2.0", await FactAsync(app, movieId, "audio_channels"));

        // The outside app puts a different file at the same place and says so.
        await File.WriteAllBytesAsync(path, new byte[3000]);
        current = Read("hevc", "truehd", 6, "5.1");
        using var told = await app.Client.PostAsJsonAsync("/api/integrations/external/file-changed", new { path, tool = "Weir", reason = "Swapped the file." });
        Assert.Equal(HttpStatusCode.Accepted, told.StatusCode);
        var queued = (await app.Services.GetRequiredService<IJobQueueRepository>().ListAsync(100, CancellationToken.None))
            .Single(job => job.JobType == "library.media.probe" && job.Source == "external");
        Assert.Equal("H.264", await FactAsync(app, movieId, "video_codec"));

        await probe.HandleAsync(queued, CancellationToken.None);

        Assert.Equal("HEVC", await FactAsync(app, movieId, "video_codec"));
        Assert.Equal("TrueHD", await FactAsync(app, movieId, "audio_codec"));
        Assert.Equal("5.1", await FactAsync(app, movieId, "audio_channels"));
    }

    private static async Task<string?> FactAsync(ApplicationTestHost app, string movieId, string column)
        => await ApiScenes.TextAsync(app, "movies", $"SELECT {column} FROM movie_wanted_state WHERE movie_id = '{movieId}';");
}
