using System.Net;
using System.Text;
using Deluno.Connections;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1251: a wrong SABnzbd API key reached the person only as "SABnzbd returned 403". A key or login a client refuses
/// reads, on every client and on every path (an action, a removal, the connection test), in plain words naming the client
/// and what was refused, and where to check it:
/// "SABnzbd refused Deluno's API key; check it in the client's settings", and for a login "refused Deluno's username and
/// password" (Deluge has only a password). The sentence ends with the check that finds the fault (Test connection).
///
/// <para>Replies are recordings: SABnzbd 5.1.3 answers every call made with a wrong key 403 "API Key Incorrect"
/// (api-key-refused); qBittorrent 5.2.1 answers a wrong sign-in 401 "Unauthorized" (login-refused); Deluge 2.2.0 answers
/// auth.login false (login-refused). NZBGet and Transmission have no recording of a refused login (NZBGet's
/// login-refused recording holds only its open version call), so they are answered with the bare HTTP 401 a client behind
/// Basic authentication sends; that is the one place a status is used without a recording, and it carries no body.</para>
/// </summary>
public sealed class ARefusedKeyOrLoginIsSaidPlainlyTests
{
    private const string Where = "Test connection";

    private static readonly string[] SabActions =
    [
        DownloadClientActions.Pause, DownloadClientActions.Resume, DownloadClientActions.Delete,
        DownloadClientActions.DeleteWithData, DownloadClientActions.RemoveRefusedCopy, DownloadClientActions.MoveToTop
    ];

    public static IEnumerable<object[]> SabnzbdActions() => SabActions.Select(action => new object[] { action });

    private static DownloadClientItem Client(string protocol, string? username = null)
        => new("client-1", protocol, protocol, "localhost", 8080, username, "wrong-secret", "http://localhost:8080/", false, null,
            "movies", "tv", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static void AssertSaysSo(DownloadClientActionResult result, string expectedStart)
    {
        Assert.False(result.Succeeded);
        Assert.StartsWith(expectedStart, result.Message);
        Assert.Contains(Where, result.Message);
        Assert.DoesNotContain("403", result.Message);
        Assert.DoesNotContain("401", result.Message);
        Assert.Equal(IntegrationFailureKind.Authentication, result.Failure!.Kind);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>Every call is answered with this one reply: a client with a wrong key or login answers all of them alike.</summary>
    private static HttpMessageHandler AnsweringEverythingWith(RecordedReply reply)
        => RecordedReplies.Handler("none", [], (_, _) => reply.ToResponse());

    private static HttpMessageHandler AnsweringEverythingWithStatus(HttpStatusCode status)
        => RecordedReplies.Handler("none", [], (_, _) => new HttpResponseMessage(status));

    // ---------------------------------------------------------------- SABnzbd

    [Theory]
    [MemberData(nameof(SabnzbdActions))]
    public async Task A_wrong_SABnzbd_key_is_said_plainly_on_every_action(string action)
    {
        var refused = RecordedReplies.Sabnzbd("api-key-refused").Find("mode=queue&output=json").Reply;
        var handler = AnsweringEverythingWith(refused);

        var result = await new SabnzbdDownloadClient(new Factory(handler))
            .ExecuteActionAsync(Client("sabnzbd"), action, "7690dfab-c8ad-4a5e-92cd-427a14ce4ea6", CancellationToken.None);

        AssertSaysSo(result, "SABnzbd refused Deluno's API key; check it in the client's settings");
    }

    [Fact]
    public async Task A_wrong_SABnzbd_key_is_said_plainly_by_the_connection_test()
    {
        using var server = new RecordedServer(RecordedReplies.Sabnzbd("api-key-refused").Find("mode=queue&output=json").Reply);

        var test = await ConnectionsEndpointRouteBuilderExtensions.TestDownloadClientAsync(server.Item("sabnzbd"), CancellationToken.None);

        Assert.Equal("degraded", test.healthStatus);
        Assert.StartsWith("SABnzbd refused Deluno's API key; check it in the client's settings", test.message);
        Assert.Contains(Where, test.message);
        Assert.Equal("auth", test.failureCategory);
    }

    // ------------------------------------------------------------ qBittorrent

    [Theory]
    [InlineData(DownloadClientActions.Pause)]
    [InlineData(DownloadClientActions.Resume)]
    [InlineData(DownloadClientActions.DeleteWithData)]
    public async Task A_wrong_qBittorrent_login_is_said_plainly_on_every_action(string action)
    {
        var handler = AnsweringEverythingWith(RecordedReplies.Qbittorrent("login-refused").Single.Reply);

        var result = await new QbittorrentDownloadClient(() => handler)
            .ExecuteActionAsync(Client("qbittorrent", "deluno"), action, "8d878a21c3d4180ae08b4e044904667d9f30bec5", CancellationToken.None);

        AssertSaysSo(result, "qBittorrent refused Deluno's username and password; check them in the client's settings");
    }

    [Fact]
    public async Task A_wrong_qBittorrent_login_is_said_plainly_by_the_connection_test()
    {
        using var server = new RecordedServer(RecordedReplies.Qbittorrent("login-refused").Single.Reply);

        var test = await ConnectionsEndpointRouteBuilderExtensions.TestDownloadClientAsync(server.Item("qbittorrent", "deluno"), CancellationToken.None);

        Assert.Equal("degraded", test.healthStatus);
        Assert.StartsWith("qBittorrent refused Deluno's username and password; check them in the client's settings", test.message);
        Assert.Contains(Where, test.message);
        Assert.Equal("auth", test.failureCategory);
    }

    // ----------------------------------------------------------------- Deluge

    [Theory]
    [InlineData(DownloadClientActions.Pause)]
    [InlineData(DownloadClientActions.Resume)]
    [InlineData(DownloadClientActions.DeleteWithData)]
    public async Task A_wrong_Deluge_password_is_said_plainly_on_every_action(string action)
    {
        var handler = AnsweringEverythingWith(RecordedReplies.Deluge("login-refused").Single.Reply);

        var result = await new DelugeDownloadClient(new Factory(handler))
            .ExecuteActionAsync(Client("deluge"), action, "b7b95339b09b0e7249d2b51ecc500b1650a18592", CancellationToken.None);

        AssertSaysSo(result, "Deluge refused Deluno's password; check it in the client's settings");
    }

    [Fact]
    public async Task A_wrong_Deluge_password_is_said_plainly_by_the_connection_test()
    {
        using var server = new RecordedServer(RecordedReplies.Deluge("login-refused").Single.Reply);

        var test = await ConnectionsEndpointRouteBuilderExtensions.TestDownloadClientAsync(server.Item("deluge"), CancellationToken.None);

        Assert.Equal("degraded", test.healthStatus);
        Assert.StartsWith("Deluge refused Deluno's password; check it in the client's settings", test.message);
        Assert.Contains(Where, test.message);
        Assert.Equal("auth", test.failureCategory);
    }

    // ------------------------------------------------- NZBGet and Transmission

    [Theory]
    [InlineData(DownloadClientActions.Pause)]
    [InlineData(DownloadClientActions.Resume)]
    [InlineData(DownloadClientActions.DeleteWithData)]
    [InlineData(DownloadClientActions.MoveToTop)]
    public async Task A_wrong_NZBGet_login_is_said_plainly_on_every_action(string action)
    {
        var handler = AnsweringEverythingWithStatus(HttpStatusCode.Unauthorized);

        var result = await new NzbGetDownloadClient(new Factory(handler), removalSettle: TimeSpan.Zero)
            .ExecuteActionAsync(Client("nzbget", "nzbget"), action, "26", CancellationToken.None);

        AssertSaysSo(result, "NZBGet refused Deluno's username and password; check them in the client's settings");
    }

    [Theory]
    [InlineData(DownloadClientActions.Pause)]
    [InlineData(DownloadClientActions.Resume)]
    [InlineData(DownloadClientActions.DeleteWithData)]
    [InlineData(DownloadClientActions.MoveToTop)]
    public async Task A_wrong_Transmission_login_is_said_plainly_on_every_action(string action)
    {
        var handler = AnsweringEverythingWithStatus(HttpStatusCode.Unauthorized);

        var result = await new TransmissionDownloadClient(new Factory(handler))
            .ExecuteActionAsync(Client("transmission", "transmission"), action, "ff7447384425ba526a3efb9efb792f7ba67a77dd", CancellationToken.None);

        AssertSaysSo(result, "Transmission refused Deluno's username and password; check them in the client's settings");
    }

    [Theory]
    [InlineData("nzbget", "NZBGet")]
    [InlineData("transmission", "Transmission")]
    public async Task A_wrong_login_is_said_plainly_by_the_connection_test(string protocol, string name)
    {
        using var server = new RecordedServer(status: HttpStatusCode.Unauthorized);

        var test = await ConnectionsEndpointRouteBuilderExtensions.TestDownloadClientAsync(server.Item(protocol, "login"), CancellationToken.None);

        Assert.Equal("degraded", test.healthStatus);
        Assert.StartsWith($"{name} refused Deluno's username and password; check them in the client's settings", test.message);
        Assert.Contains(Where, test.message);
        Assert.Equal("auth", test.failureCategory);
    }

    /// <summary>A tiny local server that answers every call with one recorded reply (or one bare status).</summary>
    private sealed class RecordedServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly int _port;

        public RecordedServer(RecordedReply? reply = null, HttpStatusCode status = HttpStatusCode.OK)
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            _port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await _listener.GetContextAsync(); }
                    catch { return; }

                    context.Response.StatusCode = reply?.Status ?? (int)status;
                    if (reply?.Body is { } body)
                    {
                        context.Response.ContentType = reply.Headers.TryGetValue("content-type", out var type) ? type : "application/json";
                        await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(body));
                    }

                    context.Response.Close();
                }
            });
        }

        public DownloadClientItem Item(string protocol, string? username = null)
            => new("c-1", protocol, protocol, "127.0.0.1", _port, username, "wrong-secret", $"http://127.0.0.1:{_port}/", false, null,
                "Movies", "TV", null, 1, true, "unknown", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        public void Dispose() => _listener.Close();
    }
}
