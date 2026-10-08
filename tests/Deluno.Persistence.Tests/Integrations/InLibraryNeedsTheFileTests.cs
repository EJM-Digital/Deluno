using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// Rig, 8 Oct (#1167): a queue row read "In library" while its episode was Missing. "In library" is a
/// claim about the title, so it needs the title or episode to hold its file.
/// </summary>
public sealed class InLibraryNeedsTheFileTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-08T00:00:00Z");
    private const string Release = "The.Twilight.Zone.1959.S01E04.1080p.WEB-DL-playWEB";

    [Theory]
    [InlineData(true, DownloadQueueStatuses.Imported)]
    [InlineData(false, DownloadQueueStatuses.ImportReady)]
    [InlineData(null, DownloadQueueStatuses.Imported)]
    public async Task A_download_reads_In_library_only_when_its_episode_has_its_file(bool? hasFile, string expected)
    {
        var result = await DownloadClientTelemetryService.DemoteImportedWithoutItsFileAsync(
            Snapshot(DownloadQueueStatuses.Imported),
            [Dispatch()],
            new Files(hasFile),
            CancellationToken.None);

        Assert.Equal(expected, Assert.Single(result.Queue).Status);
    }

    [Fact]
    public async Task A_download_that_is_not_imported_is_left_alone()
    {
        var result = await DownloadClientTelemetryService.DemoteImportedWithoutItsFileAsync(
            Snapshot(DownloadQueueStatuses.ImportQueued),
            [Dispatch()],
            new Files(false),
            CancellationToken.None);

        Assert.Equal(DownloadQueueStatuses.ImportQueued, Assert.Single(result.Queue).Status);
    }

    private sealed class Files(bool? hasFile) : IDispatchTitleUnmonitorService
    {
        public bool CanHandle(string entityType) => true;
        public Task<bool> UnmonitorAsync(string entityType, string entityId, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<bool?> HasFileAsync(string entityType, string entityId, CancellationToken cancellationToken) => Task.FromResult(hasFile);
    }

    private static DownloadClientTelemetrySnapshot Snapshot(string status)
        => new(
            "client-1", "qBittorrent", "qbittorrent", "http://localhost:8080/", "healthy", null,
            new DownloadClientTelemetryCapabilities(true, true, true, true, true, true, "session"),
            new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0),
            [new DownloadQueueItem(
                "hash-1", "client-1", "qBittorrent", "qbittorrent", "tv", "The Twilight Zone", Release, "tv",
                status, 1.0, 0, 0, 100, 100, 0, "Lab", null, Now,
                SourcePath: @"C:\Downloads\Completed\TV\" + Release)],
            [],
            Now);

    private static DownloadDispatchItem Dispatch()
        => new(
            "dispatch-1", "library-1", "tv", "episode", "episode-4", Release, "Lab", "client-1", "qBittorrent",
            "imported", null, Now, "succeeded", Now, 200, null, null, null, Now, "hash-1", 0,
            "imported", Now, Now, null, null, null, null);
}
