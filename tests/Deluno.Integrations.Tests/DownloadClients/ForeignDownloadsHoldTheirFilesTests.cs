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
/// <see cref="IDownloadClientTelemetryService.GetSnapshotsForLeftoverScanAsync"/> --
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
            // GetSnapshotsForLeftoverScanAsync returns the item exactly as the
            // client reports it, ownership untouched -- a foreign item Deluno
            // never dispatched. GetOverviewAsync is deliberately left
            // unconfigured (strict-ish): if ListAsync ever called it instead,
            // it would come back empty and wrongly report the item as an
            // orphan, failing this test.
            var telemetry = new Mock<IDownloadClientTelemetryService> { CallBase = true };
            telemetry.Setup(instance => instance.GetSnapshotsForLeftoverScanAsync(It.IsAny<CancellationToken>()))
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

    private static async Task<IReadOnlyList<OrphanedDownloadItem>> ListLeftoversAsync(string entryName, params DownloadDispatchItem[] dispatches)
    {
        var directory = Directory.CreateTempSubdirectory("leftover-proof-test-");
        try
        {
            var itemPath = Path.Combine(directory.FullName, entryName);
            Directory.CreateDirectory(itemPath);
            await File.WriteAllTextAsync(Path.Combine(itemPath, "episode.mkv"), "bytes");
            BackdatePastGrace(itemPath);

            var telemetry = new Mock<IDownloadClientTelemetryService> { CallBase = true };
            telemetry.Setup(instance => instance.GetSnapshotsForLeftoverScanAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);

            return await CreateService(Library(directory.FullName), telemetry.Object, dispatches).ListAsync(CancellationToken.None);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static DownloadDispatchItem Dispatch(string releaseName, string status)
        => ((DownloadDispatchItem)RuntimeHelpers.GetUninitializedObject(typeof(DownloadDispatchItem))) with { ReleaseName = releaseName, Status = status };

    /// <summary>#1062: an entry in a scanned folder that no dispatch of Deluno's names (Radarr's or Sonarr's, say) is never returned.</summary>
    [Fact]
    public async Task An_entry_Deluno_never_sent_is_not_returned_even_when_nothing_claims_it()
        => Assert.Empty(await ListLeftoversAsync("Radarr.Left.Behind.S01E01"));

    [Fact]
    public async Task With_no_client_claiming_it_an_entry_Deluno_sent_is_a_leftover()
        => Assert.Single(await ListLeftoversAsync("Nothing.Claims.This.S01E01", Dispatch("Nothing.Claims.This.S01E01", "completed")));

    [Fact]
    public async Task A_leftover_of_an_archived_dispatch_is_still_Deluno_own_and_is_returned()
        => Assert.Single(await ListLeftoversAsync("Old.Grab.S01E01", Dispatch("Old.Grab.S01E01", "archived")));

    /// <summary>
    /// #1062: a folder only another tool's downloads live in is never scanned,
    /// so a stray entry next to them is not listed as Deluno's leftover.
    /// </summary>
    [Fact]
    public async Task A_folder_only_another_tools_downloads_live_in_is_never_scanned()
    {
        var directory = Directory.CreateTempSubdirectory("foreign-download-folder-not-scanned-");
        try
        {
            var foreignItemPath = Path.Combine(directory.FullName, "Foreign.Tool.Added.This.S01E01");
            var strayPath = Path.Combine(directory.FullName, "Some.Other.Stray.Folder");
            Directory.CreateDirectory(foreignItemPath);
            Directory.CreateDirectory(strayPath);
            BackdatePastGrace(foreignItemPath);
            BackdatePastGrace(strayPath);

            // The library sets no download folder of its own, so the only way
            // this folder could be scanned is through the other tool's item.
            var library = Library(null);
            var telemetry = new Mock<IDownloadClientTelemetryService> { CallBase = true };
            telemetry.Setup(instance => instance.GetSnapshotsForLeftoverScanAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([ClientSnapshot(foreignItemPath)]);

            var found = await CreateService(library, telemetry.Object).ListAsync(CancellationToken.None);

            Assert.Empty(found);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Past <see cref="OrphanedDownloads.Grace"/> (2 hours), so a freshly-written test fixture is not mistaken for a download that only just finished.</summary>
    private static void BackdatePastGrace(string path)
        => Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-3));

    private static LibraryItem Library(string? downloadsPath)
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
            [],
            [],
            Now)
        {
            // #1062: what another tool added is not listed or counted; only
            // its path comes through, so the file is never swept up.
            OutsideSourcePaths = [foreignItemPath]
        };

    private static OrphanedDownloadService CreateService(LibraryItem library, IDownloadClientTelemetryService telemetryService, params DownloadDispatchItem[] dispatches)
    {
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(instance => instance.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([library]);

        var platformSettings = new Mock<IPlatformSettingsRepository>();
        platformSettings.Setup(instance => instance.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlatformSettingsSnapshot)RuntimeHelpers.GetUninitializedObject(typeof(PlatformSettingsSnapshot)));

        var jobQueue = new Mock<IJobQueueRepository>();
        jobQueue.Setup(instance => instance.ListAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        jobQueue.Setup(instance => instance.FindGrabbedReleaseNamesAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<string> names, CancellationToken _) =>
                (IReadOnlyList<string>)dispatches
                    .Select(item => item.ReleaseName)
                    .Where(release => names.Contains(release, StringComparer.OrdinalIgnoreCase))
                    .ToArray());

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
