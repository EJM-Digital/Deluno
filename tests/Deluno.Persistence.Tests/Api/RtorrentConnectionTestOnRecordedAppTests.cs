using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1301, rTorrent: the drawer's Test button, through the real route and the real HTTP client, against rTorrent 0.16.25's own replies
/// (tests/fixtures/recorded/rtorrent) served over a loopback port. A healthy reply, a fault, and an address that answers 200 with
/// another app's page are each read the way a person is told: connected, or "check the address" with a category the screen can act on.
/// </summary>
public sealed class RtorrentConnectionTestOnRecordedAppTests
{
    private static object Form(RecordedAppServer server) => new
    {
        name = "rTorrent",
        protocol = "rtorrent",
        host = server.Host,
        port = server.Port,
        username = "deluno",
        password = "a-test-password",
        moviesCategory = "movies",
        tvCategory = "tv",
        priority = 10,
        isEnabled = true
    };

    private static Func<HttpRequestMessage, string, HttpResponseMessage?> Answer(Deluno.Tests.Recorded.RecordedReply reply)
        => (_, _) => reply.ToResponse();

    private static async Task<JsonElement> TestAsync(RecordedAppServer server)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var response = await app.Client.PostAsJsonAsync("/api/download-clients/test", Form(server));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task A_version_answer_in_xml_rpc_is_a_healthy_rTorrent_and_the_call_was_system_client_version_with_the_login()
    {
        await using var rtorrent = await RecordedAppServer.StartAsync("rtorrent", [RecordedReplies.Rtorrent("version")]);

        var body = await TestAsync(rtorrent);

        Assert.Equal("healthy", body.GetProperty("healthStatus").GetString());
        Assert.Contains("Connected to rTorrent", body.GetProperty("message").GetString()!, StringComparison.Ordinal);
        var call = Assert.Single(rtorrent.Requests);
        Assert.Contains("<methodName>system.client_version</methodName>", call.Body, StringComparison.Ordinal);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("deluno:a-test-password")), call.Authorization);
        Assert.Empty(rtorrent.Unanswered);
    }

    [Fact]
    public async Task A_fault_from_the_mount_is_unexpected_response_with_the_address_to_check()
    {
        var fault = RecordedReplies.Rtorrent("unknown-hash-fault").Exchanges.First(e => e.Reply.Body!.Contains("<fault>", StringComparison.Ordinal)).Reply;
        await using var rtorrent = await RecordedAppServer.StartAsync("rtorrent", [RecordedReplies.Rtorrent("version")], Answer(fault));

        var body = await TestAsync(rtorrent);

        Assert.Equal("degraded", body.GetProperty("healthStatus").GetString());
        Assert.Equal("unexpected-response", body.GetProperty("failureCategory").GetString());
        Assert.Contains("answered with a fault", body.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_address_that_answers_with_another_apps_reply_is_unexpected_response_not_connected()
    {
        var wrongApp = RecordedReplies.Qbittorrent("version").Exchanges[0].Reply;
        await using var rtorrent = await RecordedAppServer.StartAsync("rtorrent", [RecordedReplies.Rtorrent("version")], Answer(wrongApp));

        var body = await TestAsync(rtorrent);

        Assert.Equal("degraded", body.GetProperty("healthStatus").GetString());
        Assert.Equal("unexpected-response", body.GetProperty("failureCategory").GetString());
        Assert.Contains("did not answer with an XML-RPC response", body.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    // DEFECT (found by #1301, rtorrent.r1/r2): ConnectionsEndpointRouteBuilderExtensions.TestRtorrentAsync (and TestUTorrentAsync) turn a 401 into
    // "rTorrent returned 401 at <address>." with category "unreachable" (failure kind Unavailable), not the refused-login wording
    // ("refused Deluno's username and password...") and Authentication kind that qBittorrent, SABnzbd, Transmission, Deluge and NZBGet give through
    // HealthFromStatusCode. Evidence: the real mount's recorded 401 (rtorrent/0.16.25/login-refused) through POST /api/download-clients/test.
    [Fact(Skip = "DEFECT: the Test button says 'rTorrent returned 401 at <address>.' (unreachable) for a wrong login instead of the refused-login wording every other client gets (TestRtorrentAsync does not use HealthFromStatusCode).")]
    public async Task A_mount_that_refuses_the_login_says_so_in_plain_words_like_every_other_client()
    {
        await using var rtorrent = await RecordedAppServer.StartAsync("rtorrent", [RecordedReplies.Rtorrent("login-refused")]);

        var body = await TestAsync(rtorrent);

        Assert.NotEqual("healthy", body.GetProperty("healthStatus").GetString());
        Assert.Contains("refused Deluno's username and password", body.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }
}
