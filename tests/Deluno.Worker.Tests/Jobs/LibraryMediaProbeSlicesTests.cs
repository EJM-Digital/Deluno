using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;
using Deluno.Quality.ReleasePreferences;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>
/// The media probe job's slices: nothing left to read says so by kind and queues nothing; a file ffprobe cannot read is
/// still recorded (so it is not at the front of every future pass) with nothing in it, so it never erases what the release
/// name already said; and a full slice of forty queues the next one, once per kind.
/// </summary>
public sealed class LibraryMediaProbeSlicesTests
{
    [Fact]
    public async Task With_every_file_read_the_job_says_so_for_its_kind_and_queues_nothing()
    {
        var run = Build(movieCandidates: 0);

        var movies = await run.Handler.HandleAsync(TestJobs.Create("library.media.probe", relatedEntityType: "movie"), CancellationToken.None);
        var shows = await run.Handler.HandleAsync(TestJobs.Create("library.media.probe", relatedEntityType: "series"), CancellationToken.None);

        Assert.Equal("Every file Deluno holds has been read and evaluated under its current preference plan. (Movies)", movies);
        Assert.Equal("Every file Deluno holds has been read and evaluated under its current preference plan. (TV)", shows);
        Assert.Empty(run.Queued);
    }

    [Fact]
    public async Task A_file_ffprobe_cannot_read_is_recorded_with_nothing_in_it_and_the_job_counts_it()
    {
        var run = Build(movieCandidates: 2, unreadable: ["D:\\Media\\Film 1\\Film 1.mkv"]);

        var message = await run.Handler.HandleAsync(TestJobs.Create("library.media.probe", relatedEntityType: "movie"), CancellationToken.None);

        Assert.Equal("Read 1 of 2 files; 1 could not be read. (Movies)", message);
        Assert.Equal(2, run.Written.Count);
        var unreadable = run.Written.Single(item => item.Path.EndsWith("Film 1.mkv", StringComparison.Ordinal));
        Assert.Null(unreadable.Facts.VideoCodec);
        Assert.Null(unreadable.Facts.AudioCodec);
        Assert.Null(unreadable.Facts.AudioChannels);
        var readable = run.Written.Single(item => item.Path.EndsWith("Film 2.mkv", StringComparison.Ordinal));
        Assert.Equal("HEVC", readable.Facts.VideoCodec);
    }

    [Fact]
    public async Task A_full_slice_queues_the_next_one_for_its_kind_with_a_key_that_stops_a_second_chain_stacking()
    {
        var run = Build(movieCandidates: 40);

        var message = await run.Handler.HandleAsync(TestJobs.Create("library.media.probe", relatedEntityType: "movie"), CancellationToken.None);

        Assert.Equal("Read 40 files. (Movies)", message);
        var next = Assert.Single(run.Queued);
        Assert.Equal("library.media.probe", next.JobType);
        Assert.Equal("media-probe:movie", next.DedupeKey);
        Assert.Equal("movie", next.RelatedEntityType);
    }

    [Fact]
    public async Task A_slice_that_is_not_full_queues_nothing()
    {
        var run = Build(movieCandidates: 39);

        await run.Handler.HandleAsync(TestJobs.Create("library.media.probe", relatedEntityType: "movie"), CancellationToken.None);

        Assert.Empty(run.Queued);
    }

    private sealed record Written(string Path, ProbedFileFacts Facts);

    private sealed record Run(LibraryMediaProbeJobHandler Handler, List<EnqueueJobRequest> Queued, List<Written> Written);

    private static Run Build(int movieCandidates, string[]? unreadable = null)
    {
        unreadable ??= [];
        var candidates = Enumerable.Range(1, movieCandidates)
            .Select(i => new MediaFileProbeCandidate($"movie-{i}", $"D:\\Media\\Film {i}\\Film {i}.mkv", 1_000, "library-1"))
            .ToArray();
        var written = new List<Written>();

        var mediaState = new Mock<IMediaStateRepository>();
        mediaState
            .Setup(repository => repository.ListFileProbeCandidatesAsync(MediaKind.Movie, It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<MediaPreferencePlanExpectation>>()))
            .ReturnsAsync(candidates);
        mediaState
            .Setup(repository => repository.ListFileProbeCandidatesAsync(MediaKind.Series, It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<MediaPreferencePlanExpectation>>()))
            .ReturnsAsync([]);
        mediaState
            .Setup(repository => repository.ListWantedByIdsAsync(It.IsAny<MediaKind>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        mediaState
            .Setup(repository => repository.GetTitleQualityProfileIdAsync(It.IsAny<MediaKind>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        mediaState
            .Setup(repository => repository.UpdateProbedFileFactsAsync(It.IsAny<MediaKind>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ProbedFileFacts>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .Callback<MediaKind, string, string, ProbedFileFacts, CancellationToken, string?>((_, _, path, facts, _, _) => written.Add(new Written(path, facts)))
            .Returns(Task.CompletedTask);

        var probe = new Mock<IMediaProbeService>();
        probe
            .Setup(service => service.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string path, CancellationToken _) => unreadable.Contains(path)
                ? new MediaProbeInfo("failed", "ffprobe", "ffprobe could not read the file.", null, null, null, [], [], [])
                : new MediaProbeInfo(
                    "succeeded", "ffprobe", null, 120, "matroska", null,
                    [new MediaVideoStreamInfo(0, "hevc", null, 1920, 1080, null, null, null, null)],
                    [new MediaAudioStreamInfo(1, "truehd", null, 6, "5.1", null, null, null)],
                    []));

        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repository => repository.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var quality = new Mock<IQualityRepository>();
        quality.Setup(repository => repository.ListQualityProfilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        quality.Setup(repository => repository.ListCustomFormatsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var queued = new List<EnqueueJobRequest>();
        var scheduler = new Mock<IJobScheduler>();
        scheduler
            .Setup(item => item.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()))
            .Callback<EnqueueJobRequest, CancellationToken>((request, _) => queued.Add(request))
            .ReturnsAsync(TestJobs.Create("library.media.probe"));

        var handler = new LibraryMediaProbeJobHandler(
            mediaState.Object, probe.Object, scheduler.Object, NullLogger<LibraryMediaProbeJobHandler>.Instance, TimeProvider.System,
            libraries.Object, quality.Object, new Mock<IReleasePreferencePlanRepository>().Object);
        return new Run(handler, queued, written);
    }
}
