using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>
/// A payload that cannot be read, a library that asks for nothing, a folder that is gone, a missing ffprobe and a
/// cancelled job: each is said in words and none of them writes a wrong scan.
/// </summary>
public sealed class TheSubtitleScanSurvivesWhatItCannotReadTests
{
    private const string Payload = "{\"libraryId\":\"library-1\"}";

    private static LibraryItem Library(IReadOnlyList<string>? languages) => new(
        Id: "library-1", Name: "Films", MediaType: "movies", Purpose: "collection", RootPath: "/films", DownloadsPath: null,
        QualityProfileId: null, QualityProfileName: null, CutoffQuality: null, UpgradeUntilCutoff: true, UpgradeUnknownItems: true,
        ImportWorkflow: "standard", ProcessorName: null, ProcessorOutputPath: null, ProcessorTimeoutMinutes: 60, ProcessorFailureMode: "hold",
        AutoSearchEnabled: false, MissingSearchEnabled: false, UpgradeSearchEnabled: false, SearchIntervalMinutes: 6, RetryDelayHours: 12,
        MaxItemsPerRun: 50, SearchWindowStartHour: null, SearchWindowEndHour: null, AutomationStatus: "active", SearchRequested: false,
        LastSearchedUtc: null, NextSearchUtc: null, CreatedUtc: DateTimeOffset.UnixEpoch, UpdatedUtc: DateTimeOffset.UnixEpoch)
    { SubtitleLanguages = languages };

    private sealed class Harness
    {
        public Mock<ILibrariesRepository> Libraries { get; } = new();
        public Mock<IMediaSubtitleRepository> Subtitles { get; } = new();
        public Mock<ISubtitleInventoryService> Inventory { get; } = new();
        public Mock<IJobScheduler> Scheduler { get; } = new();
        public List<MediaSubtitleScan> Recorded { get; } = [];

        public Harness(LibraryItem? library, IReadOnlyList<MediaSubtitleScanCandidate> candidates)
        {
            Libraries.Setup(l => l.ListLibrariesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(library is null ? [] : [library]);
            Subtitles.Setup(s => s.ListPendingScansAsync(It.IsAny<MediaKind>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(candidates);
            Subtitles.Setup(s => s.ListSubtitlesAsync(It.IsAny<MediaKind>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
            Subtitles.Setup(s => s.RecordScanAsync(It.IsAny<MediaKind>(), It.IsAny<string>(), It.IsAny<MediaSubtitleScan>(), It.IsAny<IReadOnlyList<MediaSubtitleRow>>(), It.IsAny<CancellationToken>()))
                .Callback<MediaKind, string, MediaSubtitleScan, IReadOnlyList<MediaSubtitleRow>, CancellationToken>((_, _, scan, _, _) => Recorded.Add(scan))
                .Returns(Task.CompletedTask);
        }

        public Task<string> RunAsync(string? payload = Payload, CancellationToken token = default)
        {
            var handler = new LibrarySubtitleScanJobHandler(Libraries.Object, Subtitles.Object, Inventory.Object, Scheduler.Object, TimeProvider.System);
            return handler.HandleAsync(TestJobs.Create("library.subtitles.scan", payloadJson: payload), token);
        }
    }

    private static MediaSubtitleScanCandidate Candidate(string path = "/films/One.mkv") => new("movie-1", path, 1_000, true);

    private static SubtitleInventory Inventory(bool exists, string probe) => new("/films/One.mkv", exists, probe, null, []);

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{}")]
    [InlineData("{\"libraryId\":\"  \"}")]
    public async Task A_payload_that_cannot_be_read_fails_the_job_and_reads_no_library(string payload)
    {
        var harness = new Harness(Library(["en"]), [Candidate()]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RunAsync(payload));

        harness.Libraries.Verify(l => l.ListLibrariesAsync(It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(harness.Recorded);
    }

    [Fact]
    public async Task A_library_that_asks_for_no_subtitle_languages_scans_nothing()
    {
        var harness = new Harness(Library([]), [Candidate()]);

        var message = await harness.RunAsync();

        Assert.Equal("Films is not asking for any subtitle languages, so nothing was scanned.", message);
        harness.Inventory.Verify(i => i.InspectAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_folder_that_is_gone_is_counted_as_not_on_disk_and_records_no_scan()
    {
        var harness = new Harness(Library(["en"]), [Candidate()]);
        harness.Inventory.Setup(i => i.InspectAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Inventory(exists: false, probe: "skipped"));

        var message = await harness.RunAsync();

        Assert.Equal("Read Films for subtitles. 1 tracked file(s) were not on disk.", message);
        Assert.Empty(harness.Recorded);
    }

    [Fact]
    public async Task A_missing_ffprobe_still_records_the_scan_and_says_embedded_tracks_could_not_be_read()
    {
        var harness = new Harness(Library(["en"]), [Candidate()]);
        harness.Inventory.Setup(i => i.InspectAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Inventory(exists: true, probe: "unavailable"));

        var message = await harness.RunAsync();

        Assert.Contains("ffprobe is missing from this install", message);
        var scan = Assert.Single(harness.Recorded);
        Assert.Equal("unavailable", scan.ProbeStatus);
        Assert.Equal(0, scan.SubtitleCount);
    }

    [Fact]
    public async Task A_cancelled_job_inspects_and_records_nothing()
    {
        var harness = new Harness(Library(["en"]), [Candidate()]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.RunAsync(token: new CancellationToken(true)));

        Assert.Empty(harness.Recorded);
        harness.Inventory.Verify(i => i.InspectAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
