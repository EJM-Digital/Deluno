using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// Two client behaviours the register held open (#1329), each on the client's own recorded replies: the version NZBGet 26.3 gives for
/// the connection, and uTorrent 3.6's pause of a torrent it does not hold (nothing is paused and no pause is sent).
/// </summary>
public sealed class ClientVersionAndUTorrentPauseGuardOnRecordingsTests
{
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    [Fact]
    public async Task NZBGet_shows_the_version_its_own_version_call_answers()
    {
        var recorded = RecordedReplies.Nzbget("version").Single.Reply.Body!;
        var handler = RecordedReplies.Handler("nzbget",
            [RecordedReplies.Nzbget("config-error-cleared"), RecordedReplies.Nzbget("idle"), RecordedReplies.Nzbget("version"), RecordedReplies.Nzbget("settings")]);
        var client = new DownloadClientItem("nzb-1", "NZBGet", "nzbget", "localhost", 6789, "nzbget", "tegbzn6789", "http://localhost:6789/", false, null,
            "Movies", "TV", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var snapshot = await new NzbGetDownloadClient(new Factory(handler)).GetSnapshotAsync(client, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Contains("26.3", recorded, StringComparison.Ordinal);
        Assert.Equal("26.3", snapshot!.ClientVersion);
    }

    [Theory]
    [InlineData(DownloadClientActions.Pause)]
    [InlineData(DownloadClientActions.Resume)]
    public async Task A_uTorrent_pause_or_resume_of_a_torrent_it_does_not_hold_fails_and_sends_no_change(string action)
    {
        var sent = new List<string>();
        var handler = RecordedReplies.Handler("utorrent", [RecordedReplies.Utorrent("token"), RecordedReplies.Utorrent("idle")],
            (request, _) => { sent.Add(request.RequestUri!.Query); return null; });
        var client = new DownloadClientItem("utorrent-1", "uTorrent", "utorrent", "localhost", 8081, null, null, "http://localhost:8081/", false, null, "movies", "tv", null, 1, true,
            "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var result = await new UTorrentDownloadClient(() => handler).ExecuteActionAsync(client, action, new string('0', 40), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(sent, query => query.Contains("action=pause", StringComparison.Ordinal) || query.Contains("action=start", StringComparison.Ordinal));
    }
}
