using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Quality.Data;
using Deluno.Quality.ReleasePreferences;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>
/// ffprobe missing, a file that cannot be read or has vanished, a cancelled job: the slice records what it could,
/// says how many it could not read, and a cancelled job writes nothing further.
/// </summary>
public sealed class TheMediaProbeSliceSurvivesAFileItCannotReadTests
{
    private static MediaProbeInfo Probe(string status) => new(
        Status: status, Tool: "ffprobe", Message: null, DurationSeconds: null, Container: null, Bitrate: null,
        VideoStreams: [], AudioStreams: [], SubtitleStreams: []);

    private sealed class Harness
    {
        public Mock<IMediaStateRepository> State { get; } = new();
        public Mock<IMediaProbeService> Probe { get; } = new();
        public Mock<IJobScheduler> Scheduler { get; } = new();
        public List<ProbedFileFacts> Written { get; } = [];
        public List<EnqueueJobRequest> Enqueued { get; } = [];

        public Harness(IReadOnlyList<MediaFileProbeCandidate> candidates)
        {
            State.Setup(s => s.ListFileProbeCandidatesAsync(
                    It.IsAny<MediaKind>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<MediaPreferencePlanExpectation>>()))
                .ReturnsAsync(candidates);
            State.Setup(s => s.ListWantedByIdsAsync(It.IsAny<MediaKind>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            State.Setup(s => s.GetTitleQualityProfileIdAsync(It.IsAny<MediaKind>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string?)null);
            State.Setup(s => s.UpdateProbedFileFactsAsync(
                    It.IsAny<MediaKind>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ProbedFileFacts>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
                .Callback<MediaKind, string, string, ProbedFileFacts, CancellationToken, string?>((_, _, _, facts, _, _) => Written.Add(facts))
                .Returns(Task.CompletedTask);
            Scheduler.Setup(s => s.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()))
                .Callback<EnqueueJobRequest, CancellationToken>((request, _) => Enqueued.Add(request))
                .ReturnsAsync(TestJobs.Create("library.media.probe"));
        }

        public Task<string> RunAsync(CancellationToken token = default, string? entityType = "movies")
        {
            var libraries = new Mock<ILibrariesRepository>();
            libraries.Setup(l => l.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
            var quality = new Mock<IQualityRepository>();
            quality.Setup(q => q.ListQualityProfilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
            quality.Setup(q => q.ListCustomFormatsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
            var handler = new LibraryMediaProbeJobHandler(
                State.Object, Probe.Object, Scheduler.Object, NullLogger<LibraryMediaProbeJobHandler>.Instance,
                TimeProvider.System, libraries.Object, quality.Object, new Mock<IReleasePreferencePlanRepository>().Object);
            return handler.HandleAsync(TestJobs.Create("library.media.probe", relatedEntityType: entityType), token);
        }
    }

    private static MediaFileProbeCandidate Candidate(int n) => new($"movie-{n}", $"/media/Movie {n}.mkv", 1_000, "library-1");

    [Fact]
    public async Task With_ffprobe_missing_the_file_is_stamped_with_nothing_in_it_and_the_slice_says_it_could_not_read_it()
    {
        var harness = new Harness([Candidate(1)]);
        harness.Probe.Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Probe("unavailable"));

        var message = await harness.RunAsync();

        Assert.Equal("Read 0 of 1 files; 1 could not be read. (Movies)", message);
        var written = Assert.Single(harness.Written);
        Assert.Equal(new ProbedFileFacts(null, null, null), written);
        Assert.Empty(harness.Enqueued);
    }

    [Fact]
    public async Task A_file_that_vanished_does_not_stop_the_files_after_it()
    {
        var harness = new Harness([Candidate(1), Candidate(2), Candidate(3)]);
        harness.Probe.Setup(p => p.ProbeAsync("/media/Movie 1.mkv", It.IsAny<CancellationToken>())).ReturnsAsync(Probe("unreadable"));
        harness.Probe.Setup(p => p.ProbeAsync("/media/Movie 2.mkv", It.IsAny<CancellationToken>())).ReturnsAsync(Probe("succeeded"));
        harness.Probe.Setup(p => p.ProbeAsync("/media/Movie 3.mkv", It.IsAny<CancellationToken>())).ReturnsAsync(Probe("unreadable"));

        var message = await harness.RunAsync(entityType: "series");

        Assert.Equal("Read 1 of 3 files; 2 could not be read. (TV)", message);
        Assert.Equal(3, harness.Written.Count);
    }

    [Fact]
    public async Task A_full_slice_queues_the_next_one_on_one_dedupe_key_per_kind()
    {
        var harness = new Harness(Enumerable.Range(1, 40).Select(Candidate).ToList());
        harness.Probe.Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Probe("succeeded"));

        await harness.RunAsync(entityType: "series");

        var next = Assert.Single(harness.Enqueued);
        Assert.Equal("library.media.probe", next.JobType);
        Assert.Equal("media-probe:series", next.DedupeKey);
    }

    [Fact]
    public async Task A_cancelled_job_writes_nothing_and_queues_nothing()
    {
        var harness = new Harness([Candidate(1), Candidate(2)]);
        harness.Probe.Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Probe("succeeded"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.RunAsync(new CancellationToken(true)));

        Assert.Empty(harness.Written);
        Assert.Empty(harness.Enqueued);
    }
}
