using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #878, cleanup sheet C5: Transmission's queue-level <c>error</c>/<c>errorString</c>
/// fields only ever carry Transmission's own word for a problem. The
/// tracker's own last-announce result — the one place "this torrent is
/// unregistered" actually appears — is <c>trackerStats</c>, which this
/// adapter did not request at all before #878.
/// </summary>
public sealed class TransmissionTrackerStatusTests
{
    [Fact]
    public async Task An_unregistered_announce_result_normalizes_to_the_tracker_unregistered_status()
    {
        var item = await RecordedSnapshot(hasAnnounced: true, succeeded: false, result: "torrent not registered with this tracker");

        Assert.Equal(DownloadTrackerStatuses.Unregistered, item.TrackerStatus);
    }

    [Fact]
    public async Task A_successful_announce_normalizes_to_ok()
    {
        var item = await RecordedSnapshot(hasAnnounced: true, succeeded: true, result: "Success");

        Assert.Equal(DownloadTrackerStatuses.Ok, item.TrackerStatus);
    }

    /// <summary>
    /// A tracker Transmission has not reached yet says nothing about whether
    /// it still holds the torrent — distinct from an outright rejection, and
    /// never read the same way.
    /// </summary>
    [Fact]
    public async Task An_announce_not_yet_attempted_is_not_read_as_unregistered()
    {
        var item = await RecordedSnapshot(hasAnnounced: false, succeeded: false, result: "torrent not registered with this tracker");

        Assert.Equal(DownloadTrackerStatuses.Ok, item.TrackerStatus);
    }

    /// <summary>A failed announce that is not the "gone for good" wording (a timeout, a connection refusal) is an ordinary tracker problem, not unregistered.</summary>
    [Fact]
    public async Task A_timeout_is_not_read_as_unregistered()
    {
        var item = await RecordedSnapshot(hasAnnounced: true, succeeded: false, result: "Connection timed out");

        Assert.Equal(DownloadTrackerStatuses.Ok, item.TrackerStatus);
    }

    [Fact]
    public async Task No_trackers_at_all_normalizes_to_unknown()
    {
        var item = await RecordedSnapshot(hasAnnounced: null, succeeded: null, result: null, includeTrackerStats: false);

        Assert.Equal(DownloadTrackerStatuses.Unknown, item.TrackerStatus);
    }

    private static async Task<Deluno.Integrations.DownloadClients.DownloadQueueItem> RecordedSnapshot(
        bool? hasAnnounced, bool? succeeded, string? result, bool includeTrackerStats = true)
    {
        // Transmission 4.1.3's recorded list of a download in progress, with its one tracker report as under test (or none).
        // The recorded tracker report is a working one; each value the test names is changed, and a torrent with no tracker has the report taken out.
        var torrentGet = RecordedTransmissionRig.Reply(
            RecordedTransmissionRig.Downloading,
            includeTrackerStats
                ? new (string, object?)[]
                {
                    ("trackerStats.0.hasAnnounced", hasAnnounced), ("trackerStats.0.lastAnnounceSucceeded", succeeded), ("trackerStats.0.lastAnnounceResult", result),
                    ("rateDownload", 0), ("eta", 0)
                }
                : [("trackerStats", null), ("rateDownload", 0), ("eta", 0)]);
        var handler = RecordedTransmissionRig.Handler(torrentGet);
        var client = new TransmissionDownloadClient(new Factory(handler));
        var snapshot = await client.GetSnapshotAsync(Client(), DateTimeOffset.UtcNow, CancellationToken.None);
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
