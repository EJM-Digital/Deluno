using System.Text.Json.Nodes;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;
using static Deluno.Integrations.Tests.DownloadClients.RecordedClientCalls;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1301: how Deluno starts a conversation with Transmission 4.1.3 (the 409 that hands out a session id, then the same call
/// again with it) and Deluge 2.2.0 (<c>auth.login</c>, then <c>web.connected</c>), how a link or a magnet is handed to
/// Transmission, and what Transmission says about labels before and after RPC 17. Every reply is the client's own recorded one.
/// </summary>
public sealed class TorrentClientHandshakeAndAddsOnRecordingsTests
{
    private const string Magnet = "magnet:?xt=urn:btih:ff7447384425ba526a3efb9efb792f7ba67a77dd&dn=Example";
    private const string Link = "http://indexer.test/download/1.torrent";

    private sealed class RecordedTap(HttpMessageHandler inner, List<(string Body, string? SessionId)> seen) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            seen.Add((body, request.Headers.TryGetValues("X-Transmission-Session-Id", out var values) ? values.First() : null));
            return await base.SendAsync(request, cancellationToken);
        }
    }

    [Fact]
    public async Task Transmission_asked_for_the_first_time_is_refused_with_409_and_the_same_call_is_sent_again_with_the_session_id_it_gave()
    {
        var handed = RecordedReplies.Transmission("session-handshake").Exchanges[0].Reply.Headers["x-transmission-session-id"];
        var seen = new List<(string Body, string? SessionId)>();
        var handler = new RecordedTap(
            RecordedReplies.Transmission("session-handshake").Sequence(RecordedReplies.Transmission("idle"), RecordedReplies.Transmission("settings")), seen);

        var snapshot = await new TransmissionDownloadClient(new Factory(handler))
            .GetSnapshotAsync(Client("transmission"), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal("healthy", snapshot!.HealthStatus);
        var first = seen.First(call => call.Body.Contains("\"session-get\"", StringComparison.Ordinal));
        Assert.Null(first.SessionId);
        var again = seen.Where(call => call.Body.Contains("\"session-get\"", StringComparison.Ordinal)).Skip(1).First();
        Assert.Equal(handed, again.SessionId);
    }

    [Fact]
    public async Task Deluge_with_the_right_password_logs_in_then_asks_whether_its_daemon_is_connected_and_is_healthy()
    {
        var calls = new Calls("deluge", [RecordedReplies.Deluge("sign-in"), RecordedReplies.Deluge("version"), RecordedReplies.Deluge("settings"), RecordedReplies.Deluge("idle")]);
        var handler = calls.Build();

        var snapshot = await new DelugeDownloadClient(new Factory(handler))
            .GetSnapshotAsync(Client("deluge", null, "secret"), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal("healthy", snapshot!.HealthStatus);
        Assert.True(calls.Sent("auth.login"));
        Assert.True(calls.Sent("web.connected"));
        Assert.True(calls.Log.FindIndex(line => line.Contains("auth.login", StringComparison.Ordinal)) < calls.Log.FindIndex(line => line.Contains("web.update_ui", StringComparison.Ordinal)),
            "the login comes before the list is asked for");
    }

    [Theory]
    [InlineData(Magnet)]
    [InlineData(Link)]
    public async Task Transmission_is_given_a_link_or_a_magnet_as_the_filename_of_the_add(string address)
    {
        var calls = new Calls("transmission", [RecordedReplies.Transmission("settings"), RecordedReplies.Transmission("finished-single-file"), RecordedReplies.Transmission("label-set")]);
        var handler = calls.Build();
        var request = new DownloadClientGrabRequest("Example.Movie.2026.1080p", address, "movies", "movies", "Example Indexer");

        await new TransmissionDownloadClient(new Factory(handler)).GrabAsync(Client("transmission"), request, CancellationToken.None);

        var add = calls.Log.Single(line => line.Contains("\"torrent-add\"", StringComparison.Ordinal));
        var body = JsonNode.Parse(add[(add.IndexOf('{'))..])!;
        Assert.Equal(address, body["arguments"]!["filename"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_transmission_that_knows_labels_says_the_label_is_made_when_first_used_and_asks_only_for_its_rpc_version()
    {
        var calls = new Calls("transmission", [RecordedReplies.Transmission("settings")]);
        var handler = calls.Build();

        var check = await new TransmissionDownloadClient(new Factory(handler))
            .CheckKeepSeedingCategoryAsync(Client("transmission"), "keep-seeding", CancellationToken.None);

        Assert.Equal(DownloadClientCategoryStatuses.Ready, check.Status);
        Assert.True(check.Supported);
        Assert.Contains("\"rpc-version\"", calls.Log.First(line => line.Contains("session-get", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_transmission_older_than_rpc_17_says_it_is_too_old_for_labels_and_the_check_is_unsupported()
    {
        var old = RecordedReplies.Transmission("settings").Body("session-get", ("arguments.rpc-version", 15));
        var handler = RecordedReplies.Handler("transmission", [RecordedReplies.Transmission("settings")],
            (_, body) => body.Contains("\"session-get\"", StringComparison.Ordinal) ? RecordedReplies.Json(old) : null);

        var check = await new TransmissionDownloadClient(new Factory(handler))
            .CheckKeepSeedingCategoryAsync(Client("transmission"), "keep-seeding", CancellationToken.None);

        Assert.Equal(DownloadClientCategoryStatuses.Unsupported, check.Status);
        Assert.False(check.Supported);
        Assert.Contains("too old for labels", check.Message, StringComparison.Ordinal);
    }
}
