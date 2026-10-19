using System.Net;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using static Deluno.Integrations.Tests.DownloadClients.RecordedClientCalls;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1301: a download client that does not answer in time (the timeout each client sets, here the exception a timed-out HttpClient
/// throws) and one that says "slow down" (HTTP 429 with Retry-After). Neither is a download answer: the poll lets it reach the caller,
/// which is what marks the client unreachable instead of showing an empty queue, a pause says it was not done, and a grab is never
/// reported sent. A timeout or a 429 carries no body of an app's own, so the status is all the test builds.
/// </summary>
public sealed class EveryClientTimeoutAndRateLimitTests
{
    private static readonly string[] Apps = ["qbittorrent", "transmission", "deluge", "sabnzbd", "nzbget", "rtorrent"];

    public static IEnumerable<object[]> EveryClient() => Apps.Select(app => new object[] { app });

    private sealed class Capturing(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpClient> Clients { get; } = [];

        public int Calls { get; private set; }

        public HttpClient Create() { var client = new HttpClient(this, disposeHandler: false); Clients.Add(client); return client; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(answer(request));
        }
    }

    private sealed class CapturingFactory(Capturing handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => handler.Create();
    }

    private static HttpResponseMessage TimedOut(HttpRequestMessage _)
        => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException());

    private static HttpResponseMessage SlowDown(HttpRequestMessage _)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(string.Empty) };
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
        return response;
    }

    private static IDownloadClient ClientFor(string app, Capturing handler) => app switch
    {
        "qbittorrent" => new QbittorrentDownloadClient(() => handler),
        "transmission" => new TransmissionDownloadClient(new CapturingFactory(handler)),
        "deluge" => new DelugeDownloadClient(new CapturingFactory(handler)),
        "sabnzbd" => new SabnzbdDownloadClient(new CapturingFactory(handler)),
        "nzbget" => new NzbGetDownloadClient(new CapturingFactory(handler)),
        _ => new RtorrentDownloadClient(new CapturingFactory(handler))
    };

    private static DownloadClientItem Item(string app) => Client(app, app is "sabnzbd" ? null : "deluno", "secret");

    private static string IdFor(string app) => app == "nzbget" ? "26" : "abc123abc123abc123abc123abc123abc123abc1";

    private static DownloadClientGrabRequest Request() => new("Example.Movie.2026.1080p", "http://indexer.test/download/1", "movies", "movies", "Example Indexer");

    [Theory]
    [MemberData(nameof(EveryClient))]
    public async Task A_client_that_does_not_answer_in_time_fails_the_poll_instead_of_showing_an_empty_queue(string app)
    {
        var handler = new Capturing(TimedOut);

        var error = await Record.ExceptionAsync(() => ClientFor(app, handler).GetSnapshotAsync(Item(app), DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.True(error is TaskCanceledException or TimeoutException or HttpRequestException, $"{app}: {error?.ToString() ?? "no error, the poll answered"}");
    }

    [Theory]
    [MemberData(nameof(EveryClient))]
    public async Task A_client_that_does_not_answer_in_time_is_never_told_a_pause_was_done_or_a_grab_was_sent(string app)
    {
        var client = ClientFor(app, new Capturing(TimedOut));

        var pause = await Record.ExceptionAsync(async () => Assert.False((await client.ExecuteActionAsync(Item(app), DownloadClientActions.Pause, IdFor(app), CancellationToken.None)).Succeeded));
        var grab = await Record.ExceptionAsync(async () => Assert.False((await client.GrabAsync(Item(app), Request(), CancellationToken.None)).Succeeded));

        Assert.True(pause is null or TaskCanceledException or TimeoutException or HttpRequestException, $"{app} pause: {pause}");
        Assert.True(grab is null or TaskCanceledException or TimeoutException or HttpRequestException, $"{app} grab: {grab}");
    }

    [Theory]
    [InlineData("transmission", 8)]
    [InlineData("deluge", 20)]
    [InlineData("sabnzbd", 8)]
    [InlineData("nzbget", 8)]
    [InlineData("rtorrent", 8)]
    public async Task Every_client_sets_its_own_short_timeout_instead_of_waiting_the_hundred_seconds_of_an_unset_one(string app, int seconds)
    {
        var handler = new Capturing(TimedOut);

        await Record.ExceptionAsync(() => ClientFor(app, handler).GetSnapshotAsync(Item(app), DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.NotEmpty(handler.Clients);
        Assert.All(handler.Clients, http => Assert.Equal(TimeSpan.FromSeconds(seconds), http.Timeout));
    }

    [Theory]
    [MemberData(nameof(EveryClient))]
    public async Task A_client_that_says_slow_down_fails_the_poll_with_the_429_instead_of_showing_an_empty_queue(string app)
    {
        var handler = new Capturing(SlowDown);

        var error = await Record.ExceptionAsync(() => ClientFor(app, handler).GetSnapshotAsync(Item(app), DateTimeOffset.UtcNow, CancellationToken.None));

        var http = Assert.IsAssignableFrom<HttpRequestException>(error);
        Assert.Equal(HttpStatusCode.TooManyRequests, http.StatusCode);
    }

    [Theory]
    [MemberData(nameof(EveryClient))]
    public async Task A_client_that_says_slow_down_is_never_told_a_pause_was_done_or_a_grab_was_sent(string app)
    {
        var client = ClientFor(app, new Capturing(SlowDown));

        var pause = await Record.ExceptionAsync(async () => Assert.False((await client.ExecuteActionAsync(Item(app), DownloadClientActions.Pause, IdFor(app), CancellationToken.None)).Succeeded));
        var grab = await Record.ExceptionAsync(async () => Assert.False((await client.GrabAsync(Item(app), Request(), CancellationToken.None)).Succeeded));

        Assert.True(pause is null or HttpRequestException, $"{app} pause: {pause}");
        Assert.True(grab is null or HttpRequestException, $"{app} grab: {grab}");
    }
}
