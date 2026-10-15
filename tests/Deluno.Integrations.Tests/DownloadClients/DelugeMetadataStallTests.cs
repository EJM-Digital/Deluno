using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// The other half of #650: Deluge has no "no metadata" state of its own
/// (unlike qBittorrent's <c>metaDL</c>), so a magnet with no peers reports
/// plain "Downloading" for ever - the exact shape #618 fixed for a Deluge
/// torrent that never found a peer at all, extended to one that never even
/// resolves its own size.
/// </summary>
public sealed class DelugeMetadataStallTests
{
    [Fact]
    public async Task A_magnet_with_no_metadata_and_no_peers_is_stalled_after_the_stall_window()
    {
        var addedUtc = DateTimeOffset.UtcNow.AddMinutes(-40);
        var item = await RecordedSnapshot(totalSize: 0, progress: 0, peers: 0, addedUtc, capturedUtc: addedUtc.AddMinutes(40));

        Assert.Equal(DownloadQueueStatuses.Stalled, item.Status);
    }

    [Fact]
    public async Task A_fresh_magnet_with_no_metadata_is_downloading_not_stalled()
    {
        var addedUtc = DateTimeOffset.UtcNow.AddMinutes(-5);
        var item = await RecordedSnapshot(totalSize: 0, progress: 0, peers: 0, addedUtc, capturedUtc: addedUtc.AddMinutes(5));

        Assert.Equal(DownloadQueueStatuses.Downloading, item.Status);
    }

    [Fact]
    public async Task Completion_is_never_inferred_from_zero_size_alone()
    {
        var addedUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        var item = await RecordedSnapshot(totalSize: 0, progress: 100, peers: 0, addedUtc, capturedUtc: addedUtc.AddMinutes(1));

        Assert.NotEqual(DownloadQueueStatuses.ImportReady, item.Status);
    }

    private static async Task<DownloadQueueItem> RecordedSnapshot(
        long totalSize,
        double progress,
        int peers,
        DateTimeOffset addedUtc,
        DateTimeOffset capturedUtc)
    {
        // Deluge 2.2.0's recorded list of a magnet whose metadata never arrived, with the size, progress, peers and age under test.
        var handler = RecordedDelugeRig.Handler(RecordedDelugeRig.NoMetadata(
            ("label", "movies"), ("progress", progress), ("total_size", totalSize), ("num_peers", peers), ("time_added", addedUtc.ToUnixTimeSeconds())));
        var snapshot = await new DelugeDownloadClient(new Factory(handler)).GetSnapshotAsync(Client(), capturedUtc, CancellationToken.None);
        return Assert.Single(snapshot!.Queue);
    }

    private static DownloadClientItem Client()
        => new("deluge-1", "Deluge", "deluge", "localhost", 8112, null, null, "http://localhost:8112/", false, null, "movies", "tv", null, 1, true,
            "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
