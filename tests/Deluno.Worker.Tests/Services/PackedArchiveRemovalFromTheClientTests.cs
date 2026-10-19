using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Jobs.Data;
using Deluno.Tests.Recorded;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// The "Remove and delete still-packed archives from the download client" switch, against qBittorrent's own recorded
/// replies (5.2.1): off, the client is never asked to remove a release refused for being a packed archive; on, the
/// client is asked once to remove it with its files, and the refusal is marked cleared.
/// </summary>
public sealed class PackedArchiveRemovalFromTheClientTests
{
    private const string Hash = "3b1d84012faffbb5c3c8d00d92358d52f2c856eb";

    /// <summary>A recorded qBittorrent that holds the download until the delete call arrives.</summary>
    private sealed class RecordedQbittorrent
    {
        private bool has = true;

        public int Deletes { get; private set; }

        public HttpMessageHandler Handler => RecordedReplies.Handler("qbittorrent", [RecordedReplies.Qbittorrent("sign-in"), RecordedReplies.Qbittorrent("removed-with-data")], (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("torrents/delete", StringComparison.Ordinal))
            {
                Deletes++;
                has = false;
                return null;
            }

            return request.RequestUri.AbsolutePath.EndsWith("torrents/info", StringComparison.Ordinal)
                ? (has ? RecordedReplies.Qbittorrent("finished-folder") : RecordedReplies.Qbittorrent("removed-twice")).Find("torrents/info").Reply.ToResponse()
                : null;
        });
    }

    private static DownloadClientItem Client()
        => new("qbittorrent-main", "qbittorrent", "qBittorrent", "localhost", 8080, null, "secret", "http://localhost:8080/", false, null,
            "movies", "tv", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    /// <summary>The real blocklist hands over only the refusals whose reason the switch allows clearing.</summary>
    private static Mock<IBlockedReleaseRepository> Blocklist(params BlockedRelease[] all)
    {
        var blocklist = new Mock<IBlockedReleaseRepository>();
        blocklist.Setup(list => list.ListAwaitingCleanupAsync(It.IsAny<Func<string, bool>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Func<string, bool>? decide, CancellationToken _) => all.Where(release => decide?.Invoke(release.ReasonCode) ?? false).ToArray());
        return blocklist;
    }

    private static Mock<IDownloadClientTelemetryService> TelemetryOver(RecordedQbittorrent qbittorrent)
    {
        var telemetry = new Mock<IDownloadClientTelemetryService> { CallBase = true };
        telemetry.Setup(service => service.ExecuteActionAsync(It.IsAny<string>(), It.IsAny<DownloadClientActionRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (string clientId, DownloadClientActionRequest request, CancellationToken cancellationToken) =>
                await new QbittorrentDownloadClient(() => qbittorrent.Handler).ExecuteActionAsync(Client(), request.Action, request.QueueItemId, cancellationToken));
        return telemetry;
    }

    [Fact]
    public async Task With_the_switch_off_the_client_is_never_asked_to_remove_a_still_packed_archive()
    {
        var client = new RecordedQbittorrent();
        var blocklist = Blocklist(ClearingRefusedDownloadsTests.RefusedPackedArchive(Hash));

        var result = await ClearingRefusedDownloadsTests
            .Cleanup(blocklist.Object, TelemetryOver(client).Object, ClearingRefusedDownloadsTests.Sharing(), removeAndDeletePackedArchivesFromClient: false)
            .CleanUpEverythingAsync(CancellationToken.None);

        Assert.Equal(0, client.Deletes);
        Assert.Equal(0, result.Cleared);
        blocklist.Verify(list => list.MarkCleanedUpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task With_the_switch_on_the_client_removes_the_still_packed_archive_once_with_its_files_and_the_refusal_is_cleared()
    {
        var client = new RecordedQbittorrent();
        var blocklist = Blocklist(ClearingRefusedDownloadsTests.RefusedPackedArchive(Hash));

        var result = await ClearingRefusedDownloadsTests
            .Cleanup(blocklist.Object, TelemetryOver(client).Object, ClearingRefusedDownloadsTests.Sharing(), removeAndDeletePackedArchivesFromClient: true)
            .CleanUpEverythingAsync(CancellationToken.None);

        Assert.Equal(1, client.Deletes);
        Assert.Equal(1, result.Cleared);
        blocklist.Verify(list => list.MarkCleanedUpAsync("block-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task With_the_switch_on_a_copy_the_sharing_rule_still_holds_is_left_in_the_client()
    {
        var client = new RecordedQbittorrent();
        var blocklist = Blocklist(ClearingRefusedDownloadsTests.RefusedPackedArchive(Hash));

        var result = await ClearingRefusedDownloadsTests
            .Cleanup(blocklist.Object, TelemetryOver(client).Object, ClearingRefusedDownloadsTests.Sharing(Hash), removeAndDeletePackedArchivesFromClient: true)
            .CleanUpEverythingAsync(CancellationToken.None);

        Assert.Equal(0, client.Deletes);
        Assert.Equal(1, result.WaitingOnSharing);
    }
}
