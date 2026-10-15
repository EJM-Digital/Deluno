using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Recovery.Policies;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// A Transmission magnet whose metadata never arrives (#650). Found live on
/// the rig: <c>metadataPercentComplete 0.0</c>, <c>totalSize 0</c>,
/// <c>leftUntilDone 0</c> (only because the size behind it is unknown, not
/// because nothing is left), <c>peersConnected 0</c>. Deluno reported it
/// completed a second after the grab, and then it sat there for ever - no
/// stall, no blocklist, no retry.
/// </summary>
public sealed class TransmissionMetadataStallTests
{
    /// <summary>Fresh: no metadata, no peers, but not yet past the stall window.</summary>
    [Fact]
    public async Task A_fresh_magnet_with_no_metadata_is_downloading_not_stalled_or_complete()
    {
        var addedUtc = DateTimeOffset.UtcNow.AddMinutes(-5);
        var item = await RecordedSnapshot(status: 4, totalSize: 0, percentDone: 0, peers: 0, addedUtc, capturedUtc: addedUtc.AddMinutes(5));

        Assert.Equal(DownloadQueueStatuses.Downloading, item.Status);
    }

    /// <summary>
    /// Past #618's stall window with still no metadata and no peers: the same
    /// rule Deluge already got, so this shows as stalled on Transfers and
    /// Health, is clearable, and the release can be blocklisted and retried.
    /// </summary>
    [Fact]
    public async Task A_magnet_with_no_metadata_and_no_peers_is_stalled_after_the_stall_window()
    {
        var addedUtc = DateTimeOffset.UtcNow.AddMinutes(-40);
        var item = await RecordedSnapshot(status: 4, totalSize: 0, percentDone: 0, peers: 0, addedUtc, capturedUtc: addedUtc.AddMinutes(40));

        Assert.Equal(DownloadQueueStatuses.Stalled, item.Status);

        var findings = Evaluate(item, addedUtc.AddMinutes(40));
        var finding = Assert.Single(findings, finding => finding.Kind is "client-stalled" or "no-progress");
        Assert.True(Deluno.Platform.Contracts.DownloadHealthStrikes.Counts(finding.Kind));
    }

    /// <summary>A magnet that picks up even one peer keeps trying rather than stalling.</summary>
    [Fact]
    public async Task A_magnet_with_no_metadata_but_a_connected_peer_is_not_stalled()
    {
        var addedUtc = DateTimeOffset.UtcNow.AddMinutes(-40);
        var item = await RecordedSnapshot(status: 4, totalSize: 0, percentDone: 0, peers: 3, addedUtc, capturedUtc: addedUtc.AddMinutes(40));

        Assert.Equal(DownloadQueueStatuses.Downloading, item.Status);
    }

    /// <summary>
    /// Completion must require known, non-zero size and a done signal - never
    /// "nothing left" alone. Even if a client somehow reports full progress
    /// while its size is still unresolved, Deluno does not call it done.
    /// </summary>
    [Fact]
    public async Task Completion_is_never_inferred_from_zero_size_alone()
    {
        var addedUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        var item = await RecordedSnapshot(status: 4, totalSize: 0, percentDone: 1, peers: 0, addedUtc, capturedUtc: addedUtc.AddMinutes(1));

        Assert.NotEqual(DownloadQueueStatuses.ImportReady, item.Status);
        Assert.NotEqual(DownloadQueueStatuses.Completed, item.Status);
    }

    /// <summary>Once the metadata and size are known and the client says done, it is done.</summary>
    [Fact]
    public async Task A_download_with_known_size_and_full_progress_is_import_ready()
    {
        var addedUtc = DateTimeOffset.UtcNow.AddMinutes(-10);
        var item = await RecordedSnapshot(status: 4, totalSize: 5_000_000_000, percentDone: 1, peers: 0, addedUtc, capturedUtc: addedUtc.AddMinutes(10));

        Assert.Equal(DownloadQueueStatuses.ImportReady, item.Status);
    }

    private static IReadOnlyList<RecoveryHealthFinding> Evaluate(DownloadQueueItem item, DateTimeOffset capturedUtc)
        => new DownloadHealthEvaluator().Evaluate(
            new RecoveryQueueSnapshot(
                item.Status,
                item.ErrorMessage,
                item.SourcePath,
                item.SpeedMbps,
                item.AddedUtc,
                item.EtaSeconds,
                item.ReleaseName),
            capturedUtc);

    private static async Task<DownloadQueueItem> RecordedSnapshot(
        int status,
        long totalSize,
        double percentDone,
        int peers,
        DateTimeOffset addedUtc,
        DateTimeOffset capturedUtc)
    {
        // Transmission 4.1.3's recorded list of a magnet whose metadata never came, in the status, size, progress, peers and age under test.
        var torrentGet = RecordedTransmissionRig.Reply(
            RecordedTransmissionRig.NoMetadata,
            ("status", status), ("totalSize", totalSize), ("percentDone", percentDone), ("peersConnected", peers),
            ("addedDate", addedUtc.ToUnixTimeSeconds()), ("rateDownload", 0), ("rateUpload", 0), ("eta", 0));
        var handler = RecordedTransmissionRig.Handler(torrentGet);
        var client = new TransmissionDownloadClient(new Factory(handler));
        var snapshot = await client.GetSnapshotAsync(Client(), capturedUtc, CancellationToken.None);
        return Assert.Single(snapshot!.Queue);
    }

    private static DownloadClientItem Client()
        => new("transmission-1", "Transmission", "transmission", "localhost", 9091, null, null, "http://localhost:9091/", false, null, "movies", "tv", null, 1, true,
            "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
