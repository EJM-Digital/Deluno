using System.Net;
using System.Text;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1194: the "skip while the client's alternate speed limit is on" option also works on Transmission, which calls
/// it alt-speed (turtle mode) and reports it in session-get.
/// </summary>
public sealed class TransmissionAlternateSpeedLimitTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public async Task The_snapshot_carries_transmissions_alt_speed_state(string altSpeedEnabled, bool expected)
    {
        var snapshot = await SnapshotAsync($"{{\"result\":\"success\",\"arguments\":{{\"download-dir\":\"/downloads\",\"version\":\"4.0.5 (abc)\",\"alt-speed-enabled\":{altSpeedEnabled}}}}}");

        Assert.Equal(expected, snapshot.AlternateSpeedLimitEnabled);
    }

    [Fact]
    public async Task A_daemon_that_does_not_say_leaves_it_unknown_and_the_option_never_skips()
    {
        var snapshot = await SnapshotAsync("{\"result\":\"success\",\"arguments\":{\"download-dir\":\"/downloads\"}}");

        Assert.Null(snapshot.AlternateSpeedLimitEnabled);
    }

    private static async Task<DownloadClientTelemetrySnapshot> SnapshotAsync(string sessionGet)
    {
        var client = new TransmissionDownloadClient(new Factory(new Handler(sessionGet)));
        var snapshot = await client.GetSnapshotAsync(
            new("transmission-1", "Transmission", "transmission", "localhost", 9091, null, null, "http://localhost:9091/", false, null, "movies", "tv", null, 1, true,
                "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            DateTimeOffset.UtcNow, CancellationToken.None);
        return snapshot!;
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Handler(string sessionGet) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var reply = body.Contains("\"torrent-get\"", StringComparison.Ordinal) ? "{\"result\":\"success\",\"arguments\":{\"torrents\":[]}}"
                : body.Contains("\"session-get\"", StringComparison.Ordinal) ? sessionGet
                : "{\"result\":\"success\",\"arguments\":{}}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }
}
