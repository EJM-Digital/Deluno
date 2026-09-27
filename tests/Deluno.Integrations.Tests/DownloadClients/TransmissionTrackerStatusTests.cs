using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients.Clients;

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
        var item = await Snapshot(hasAnnounced: true, succeeded: false, result: "torrent not registered with this tracker");

        Assert.Equal(DownloadTrackerStatuses.Unregistered, item.TrackerStatus);
    }

    [Fact]
    public async Task A_successful_announce_normalizes_to_ok()
    {
        var item = await Snapshot(hasAnnounced: true, succeeded: true, result: "Success");

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
        var item = await Snapshot(hasAnnounced: false, succeeded: false, result: "torrent not registered with this tracker");

        Assert.Equal(DownloadTrackerStatuses.Ok, item.TrackerStatus);
    }

    /// <summary>A failed announce that is not the "gone for good" wording (a timeout, a connection refusal) is an ordinary tracker problem, not unregistered.</summary>
    [Fact]
    public async Task A_timeout_is_not_read_as_unregistered()
    {
        var item = await Snapshot(hasAnnounced: true, succeeded: false, result: "Connection timed out");

        Assert.Equal(DownloadTrackerStatuses.Ok, item.TrackerStatus);
    }

    [Fact]
    public async Task No_trackers_at_all_normalizes_to_unknown()
    {
        var item = await Snapshot(hasAnnounced: null, succeeded: null, result: null, includeTrackerStats: false);

        Assert.Equal(DownloadTrackerStatuses.Unknown, item.TrackerStatus);
    }

    private static async Task<Deluno.Integrations.DownloadClients.DownloadQueueItem> Snapshot(
        bool? hasAnnounced, bool? succeeded, string? result, bool includeTrackerStats = true)
    {
        var trackerStats = includeTrackerStats
            ? $",\"trackerStats\":[{{\"hasAnnounced\":{Bool(hasAnnounced)},\"lastAnnounceSucceeded\":{Bool(succeeded)},\"lastAnnounceResult\":{Json(result)}}}]"
            : string.Empty;
        var torrentGet = "{\"result\":\"success\",\"arguments\":{\"torrents\":[{\"id\":7,\"name\":\"Sherlock.Jr.1924.1080p.BluRay\","
            + "\"status\":4,\"percentDone\":0.4,"
            + "\"totalSize\":5000000000,\"downloadedEver\":2000000000,\"peersConnected\":1,\"peersSendingToUs\":1,"
            + $"\"addedDate\":{DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds()},\"rateDownload\":0,\"rateUpload\":0,\"eta\":0,"
            + "\"isStalled\":false,\"error\":0"
            + trackerStats
            + "}]}}";
        var handler = new TransmissionHandler(torrentGet);
        var client = new TransmissionDownloadClient(new Factory(handler));
        var snapshot = await client.GetSnapshotAsync(Client(), DateTimeOffset.UtcNow, CancellationToken.None);
        return Assert.Single(snapshot!.Queue);
    }

    private static string Bool(bool? value) => value is null ? "null" : value.Value ? "true" : "false";
    private static string Json(string? value) => value is null ? "null" : System.Text.Json.JsonSerializer.Serialize(value);

    private static DownloadClientItem Client()
        => new("transmission-1", "Transmission", "transmission", "localhost", 9091, null, null, "http://localhost:9091/", false, null, "movies", "tv", null, 1, true,
            "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class TransmissionHandler(string torrentGet) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var reply = body.Contains("\"torrent-get\"", StringComparison.Ordinal)
                ? torrentGet
                : "{\"result\":\"success\",\"arguments\":{}}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }
}
