using System.Runtime.CompilerServices;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #958 (cleanup sheet M2, Phase 4 re-check of #932/#933): a leftover left by
/// another download tool, or added to the client by hand, still holds its
/// file. <see cref="IOrphanedDownloadService.ListAsync"/> must read the
/// client's queue and history exactly as the client reports them --
/// <see cref="IDownloadClientTelemetryService.GetUnfilteredSnapshotsAsync"/> --
/// never <see cref="IDownloadClientTelemetryService.GetOverviewAsync"/>,
/// whose Queue is already narrowed to Deluno's own dispatches for the Queue
/// screen (#933) and so would let a foreign item's file look unclaimed.
/// </summary>
public sealed class ForeignDownloadsHoldTheirFilesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-28T12:00:00Z");

    [Fact]
    public async Task An_item_another_tool_added_to_the_client_still_holds_its_file()
    {
        var directory = Directory.CreateTempSubdirectory("foreign-download-holds-file-test-");
        try
        {
            var foreignItemPath = Path.Combine(directory.FullName, "Foreign.Tool.Added.This.S01E01");
            Directory.CreateDirectory(foreignItemPath);
            await File.WriteAllTextAsync(Path.Combine(foreignItemPath, "episode.mkv"), "bytes");
            BackdatePastGrace(foreignItemPath);

            var library = Library(directory.FullName);

            // The mocked telemetry service stands in for real client polling:
            // GetUnfilteredSnapshotsAsync returns the item exactly as the
            // client reports it, ownership untouched -- a foreign item Deluno
            // never dispatched. GetOverviewAsync is deliberately left
            // unconfigured (strict-ish): if ListAsync ever called it instead,
            // it would come back empty and wrongly report the item as an
            // orphan, failing this test.
            var telemetry = new Mock<IDownloadClientTelemetryService>();
            telemetry.Setup(instance => instance.GetUnfilteredSnapshotsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([ClientSnapshot(foreignItemPath)]);

            var service = CreateService(library, telemetry.Object);

            var found = await service.ListAsync(CancellationToken.None);

            Assert.Empty(found);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task With_no_client_claiming_it_the_same_entry_is_a_leftover()
    {
        var directory = Directory.CreateTempSubdirectory("foreign-download-no-claim-test-");
        try
        {
            var itemPath = Path.Combine(directory.FullName, "Nothing.Claims.This.S01E01");
            Directory.CreateDirectory(itemPath);
            await File.WriteAllTextAsync(Path.Combine(itemPath, "episode.mkv"), "bytes");
            BackdatePastGrace(itemPath);

            var library = Library(directory.FullName);

            var telemetry = new Mock<IDownloadClientTelemetryService>();
            telemetry.Setup(instance => instance.GetUnfilteredSnapshotsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);

            var service = CreateService(library, telemetry.Object);

            var found = await service.ListAsync(CancellationToken.None);

            Assert.Single(found);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Past <see cref="OrphanedDownloads.Grace"/> (2 hours), so a freshly-written test fixture is not mistaken for a download that only just finished.</summary>
    private static void BackdatePastGrace(string path)
        => Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-3));

    private static LibraryItem Library(string downloadsPath)
        => new(
            "library-1", "TV", "tv", "main", @"C:\Deluno\Library\TV", downloadsPath, null, null, null,
            true, true, "direct", null, null, 0, "block",
            false, false, false, 6, 6, 10, null, null, "active", false, null, null, Now, Now);

    private static DownloadClientTelemetrySnapshot ClientSnapshot(string foreignItemPath)
        => new(
            "client-1",
            "Some Other Tool's qBittorrent",
            "qbittorrent",
            "http://localhost:8080",
            "healthy",
            null,
            new DownloadClientTelemetryCapabilities(true, true, true, true, true, true, "form"),
            new DownloadTelemetrySummary(1, 0, 0, 0, 0, 0, 0),
            [
                new DownloadQueueItem(
                    "foreign-hash",
                    "client-1",
                    "Some Other Tool's qBittorrent",
                    "qbittorrent",
                    "tv",
                    "Foreign Tool Added This",
                    "Foreign.Tool.Added.This.S01E01",
                    "tv",
                    "downloading",
                    0.6,
                    0,
                    0,
                    4096,
                    2048,
                    0,
                    "",
                    null,
                    Now,
                    SourcePath: foreignItemPath)
            ],
            [],
            Now);

    private static OrphanedDownloadService CreateService(LibraryItem library, IDownloadClientTelemetryService telemetryService)
    {
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(instance => instance.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([library]);

        var platformSettings = new Mock<IPlatformSettingsRepository>();
        platformSettings.Setup(instance => instance.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlatformSettingsSnapshot)RuntimeHelpers.GetUninitializedObject(typeof(PlatformSettingsSnapshot)));

        var jobQueue = new Mock<IJobQueueRepository>();
        jobQueue.Setup(instance => instance.ListAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        jobQueue.Setup(instance => instance.ListDownloadDispatchesAsync(
                It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<string?>()))
            .ReturnsAsync([]);

        var processors = new Mock<IProcessorRepository>();
        processors.Setup(instance => instance.ListProcessorHandoffsAsync(It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var keptDownloads = new Mock<IKeptDownloadsRepository>();
        keptDownloads.Setup(instance => instance.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var activityFeed = new Mock<IActivityFeedRepository>();

        return new OrphanedDownloadService(
            libraries.Object,
            platformSettings.Object,
            telemetryService,
            jobQueue.Object,
            processors.Object,
            keptDownloads.Object,
            activityFeed.Object,
            TimeProvider.System,
            NullLogger<OrphanedDownloadService>.Instance);
    }
}
