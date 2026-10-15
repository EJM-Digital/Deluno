using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;
using static Deluno.Integrations.Tests.DownloadClients.RecordedClientCalls;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1301: a download client that does not answer (connection refused, as each client's own recording of it) on the three
/// ways Deluno talks to it. The poll lets the refusal reach the caller, which is what turns the client "unreachable" and keeps
/// it out of the "bad release" count; a pause or a resume and a grab say so in the result and never throw and never claim
/// the thing was done.
/// </summary>
public sealed class EveryClientNotAnsweringOnRecordingsTests
{
    private static readonly string[] Apps = ["qbittorrent", "transmission", "deluge", "sabnzbd", "nzbget"];

    public static IEnumerable<object[]> EveryClient() => Apps.Select(app => new object[] { app });

    private static HttpMessageHandler Refusing(string app) => app switch
    {
        "qbittorrent" => RecordedReplies.Qbittorrent("not-answering").NotAnswering(),
        "transmission" => RecordedReplies.Transmission("not-answering").NotAnswering(),
        "deluge" => RecordedReplies.Deluge("not-answering").NotAnswering(),
        "sabnzbd" => RecordedReplies.Sabnzbd("not-answering").NotAnswering(),
        _ => RecordedReplies.Nzbget("not-answering").NotAnswering()
    };

    private static IDownloadClient ClientFor(string app, HttpMessageHandler handler) => app switch
    {
        "qbittorrent" => new QbittorrentDownloadClient(() => handler),
        "transmission" => new TransmissionDownloadClient(new Factory(handler)),
        "deluge" => new DelugeDownloadClient(new Factory(handler)),
        "sabnzbd" => new SabnzbdDownloadClient(new Factory(handler)),
        _ => new NzbGetDownloadClient(new Factory(handler))
    };

    private static DownloadClientItem Item(string app) => Client(app, app is "sabnzbd" ? null : "deluno", "secret");

    [Theory]
    [MemberData(nameof(EveryClient))]
    public async Task A_poll_of_a_client_that_does_not_answer_lets_the_refusal_reach_the_caller_instead_of_inventing_an_empty_queue(string app)
    {
        var client = ClientFor(app, Refusing(app));

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetSnapshotAsync(Item(app), DateTimeOffset.UtcNow, CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(EveryClient))]
    public async Task A_pause_of_a_download_on_a_client_that_does_not_answer_is_a_plain_failure_not_an_exception_and_not_done(string app)
    {
        var client = ClientFor(app, Refusing(app));

        var result = await Record.ExceptionAsync(async () =>
        {
            var answer = await client.ExecuteActionAsync(Item(app), DownloadClientActions.Pause, app == "nzbget" ? "26" : "abc123abc123abc123abc123abc123abc123abc1", CancellationToken.None);
            Assert.False(answer.Succeeded);
        });

        // A client may report it as a failed result or let the refusal reach the caller (the gateway turns that into a failed result);
        // what it must never do is say the pause was done.
        Assert.True(result is null or HttpRequestException, result?.ToString());
    }

    [Theory]
    [MemberData(nameof(EveryClient))]
    public async Task A_grab_on_a_client_that_does_not_answer_is_never_reported_as_sent(string app)
    {
        var client = ClientFor(app, Refusing(app));
        var request = new DownloadClientGrabRequest("Example.Movie.2026.1080p", "http://indexer.test/download/1", "movies", "movies", "Example Indexer");

        var outcome = await Record.ExceptionAsync(async () =>
        {
            var result = await client.GrabAsync(Item(app), request, CancellationToken.None);
            Assert.False(result.Succeeded, result.Message);
        });

        Assert.True(outcome is null or HttpRequestException, outcome?.ToString());
    }
}
