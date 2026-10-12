using System.Net;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1251 for the two clients the golden VM could not record (tests/fixtures/recorded/MISSING.md: "rtorrent: not installed in
/// the VM", "utorrent: not installed in the VM"). They get the same pause and resume guard and the same plain refused-login
/// wording as the other five, and with no recording to replay, no reply shape is written here: the guard is tested through
/// its own seams (the list read before and after, handed to <see cref="ClientPauseGuard"/> directly), the state each client
/// reads is tested on the documented status value alone, and the wiring is tested with bare HTTP statuses that carry no body.
///
/// <para>Still to record, for these two: a list that holds a running job, a paused job and no job; the pause and resume
/// replies; a refused login. Until then the guard's real reads are covered by the seams below, not by a replay.</para>
/// </summary>
public sealed class UTorrentAndRtorrentPauseAndRefusalTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef01234567";

    private static DownloadClientItem Client(string protocol)
        => new("client-1", protocol, protocol, "localhost", 8080, "user", "secret", "http://localhost:8080/", false, null,
            "movies", "tv", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    // ---------------------------------------------- the state each client reads

    [Theory]
    [InlineData(0x01 | 0x08 | 0x80, false)] // started, checked, loaded: running
    [InlineData(0x01 | 0x20 | 0x80, true)]  // paused bit set
    [InlineData(0x08 | 0x80, true)]         // not started: stopped, a resume must start it
    public void UTorrent_reads_a_paused_job_from_its_status_bits(int bits, bool paused)
        => Assert.Equal(paused, UTorrentDownloadClient.IsPaused(bits));

    [Theory]
    [InlineData(1, false)]
    [InlineData(0, true)]
    public void RTorrent_reads_a_paused_download_from_is_active(long isActive, bool paused)
        => Assert.Equal(paused, RtorrentDownloadClient.IsPaused(isActive));

    // ------------------------------------------- the guard, through its seams

    private sealed class Seams(ClientPauseReading before, ClientPauseReading after)
    {
        private int _reads;

        public int Sends { get; private set; }

        public Task<ClientPauseReading> Read() => Task.FromResult(_reads++ == 0 ? before : after);

        public Task<DownloadClientActionResult> Send(DownloadClientItem client, string action)
        {
            Sends++;
            return Task.FromResult(DownloadClientHelpers.ActionSuccess(client, Hash, action, "sent"));
        }
    }

    [Theory]
    [InlineData("utorrent", DownloadClientActions.Pause)]
    [InlineData("utorrent", DownloadClientActions.Resume)]
    [InlineData("rtorrent", DownloadClientActions.Pause)]
    [InlineData("rtorrent", DownloadClientActions.Resume)]
    public async Task The_guard_says_done_only_when_the_job_was_there_and_is_in_the_asked_state(string protocol, string action)
    {
        var pausing = action == DownloadClientActions.Pause;
        var seams = new Seams(new(ClientHas.Yes, !pausing), new(ClientHas.Yes, pausing));

        var result = await ClientPauseGuard.RunAsync(Client(protocol), Hash, action, protocol, seams.Read, () => seams.Send(Client(protocol), action));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, seams.Sends);
    }

    [Theory]
    [InlineData(DownloadClientActions.Pause)]
    [InlineData(DownloadClientActions.Resume)]
    public async Task The_guard_fails_an_unknown_job_without_sending(string action)
    {
        var seams = new Seams(new(ClientHas.No, false), new(ClientHas.No, false));

        var result = await ClientPauseGuard.RunAsync(Client("utorrent"), Hash, action, "uTorrent", seams.Read, () => seams.Send(Client("utorrent"), action));

        Assert.False(result.Succeeded);
        Assert.Contains("has no download with that id", result.Message);
        Assert.Equal(0, seams.Sends);
    }

    // ------------------------------------------- the clients really use the guard

    /// <summary>A client that cannot be asked is never told to change anything. A bare 503 carries no reply shape.</summary>
    private sealed class Unanswering : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add(request.RequestUri!.PathAndQuery);
            // uTorrent's token page is asked first; an empty page gives an empty token, and every other call is unanswered.
            return Task.FromResult(request.RequestUri.AbsolutePath.EndsWith("token.html", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    private sealed class Answering(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status));
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static Task<DownloadClientActionResult> Run(string protocol, HttpMessageHandler handler, string action)
        => protocol == "utorrent"
            ? new UTorrentDownloadClient(() => handler).ExecuteActionAsync(Client(protocol), action, Hash, CancellationToken.None)
            : new RtorrentDownloadClient(new Factory(handler)).ExecuteActionAsync(Client(protocol), action, Hash, CancellationToken.None);

    [Theory]
    [InlineData("utorrent", DownloadClientActions.Pause)]
    [InlineData("utorrent", DownloadClientActions.Resume)]
    [InlineData("rtorrent", DownloadClientActions.Pause)]
    [InlineData("rtorrent", DownloadClientActions.Resume)]
    public async Task A_client_that_cannot_be_asked_is_not_told_to_pause_or_resume(string protocol, string action)
    {
        var handler = new Unanswering();

        var result = await Run(protocol, handler, action);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(handler.Calls, call => call.Contains("action=pause", StringComparison.Ordinal) || call.Contains("action=start", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("utorrent", "uTorrent", DownloadClientActions.Pause)]
    [InlineData("utorrent", "uTorrent", DownloadClientActions.Resume)]
    [InlineData("utorrent", "uTorrent", DownloadClientActions.DeleteWithData)]
    [InlineData("rtorrent", "rTorrent", DownloadClientActions.Pause)]
    [InlineData("rtorrent", "rTorrent", DownloadClientActions.Resume)]
    [InlineData("rtorrent", "rTorrent", DownloadClientActions.Delete)]
    public async Task A_refused_login_is_said_plainly(string protocol, string name, string action)
    {
        var result = await Run(protocol, new Answering(HttpStatusCode.Unauthorized), action);

        Assert.False(result.Succeeded);
        Assert.StartsWith($"{name} refused Deluno's username and password; check them in the client's settings", result.Message);
        Assert.Contains("Test connection", result.Message);
        Assert.Equal(IntegrationFailureKind.Authentication, result.Failure!.Kind);
    }
}
