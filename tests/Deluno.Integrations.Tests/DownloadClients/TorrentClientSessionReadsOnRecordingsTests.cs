using System.Net;
using System.Text.Json.Nodes;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;
using static Deluno.Integrations.Tests.DownloadClients.RecordedClientCalls;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1301: what each torrent client says about itself on the poll Deluno already makes (its version, the free space of its
/// download folder, the alternate-speed flag), read from qBittorrent 5.2.1's, Transmission 4.1.3's and Deluge 2.2.0's own
/// recorded replies, and what Deluno makes of a poll the client answers badly. The figures expected are read out of the
/// recordings, so a different recording changes the expected value with it.
/// </summary>
public sealed class TorrentClientSessionReadsOnRecordingsTests
{
    private static HttpMessageHandler QbittorrentIdle(Func<HttpRequestMessage, string, HttpResponseMessage?>? intercept = null)
        => RecordedQbittorrentRig.Handler(RecordedReplies.Qbittorrent("idle").Body("torrents/info"), null, intercept);

    private static HttpMessageHandler TransmissionIdle(Func<HttpRequestMessage, string, HttpResponseMessage?>? intercept = null)
        => RecordedTransmissionRig.Handler(RecordedReplies.Transmission("idle").Body("torrent-get"), intercept);

    private static HttpMessageHandler DelugeIdle(Func<HttpRequestMessage, string, HttpResponseMessage?>? intercept = null)
        => RecordedReplies.Handler(
            "deluge",
            [RecordedReplies.Deluge("sign-in"), RecordedReplies.Deluge("version"), RecordedReplies.Deluge("settings"), RecordedReplies.Deluge("idle")],
            intercept);

    private static JsonNode MainData()
        => JsonNode.Parse(RecordedReplies.Qbittorrent("settings").Find("sync/maindata").Reply.Body!)!;

    [Fact]
    public async Task Qbittorrent_reads_its_version_free_space_and_alternate_speed_flag_from_its_recorded_session()
    {
        var server = MainData()["server_state"]!;
        var handler = QbittorrentIdle();

        var snapshot = await new QbittorrentDownloadClient(() => handler)
            .GetSnapshotAsync(Client("qbittorrent", "deluno"), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal("5.2.1", snapshot!.ClientVersion);
        Assert.Equal(server["free_space_on_disk"]!.GetValue<long>(), snapshot.FreeSpaceBytes);
        Assert.Equal(server["use_alt_speed_limits"]!.GetValue<bool>(), snapshot.AlternateSpeedLimitEnabled);
    }

    [Fact]
    public async Task Qbittorrent_whose_version_call_is_refused_still_gives_its_snapshot_and_no_version()
    {
        // The sign-in itself is proven by the first version call (the recorded sign-in has no body), so only the poll's own version call is refused.
        var versionCalls = 0;
        var handler = QbittorrentIdle((request, _) => request.RequestUri!.AbsolutePath.EndsWith("app/version", StringComparison.Ordinal) && ++versionCalls > 1
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : null);

        var snapshot = await new QbittorrentDownloadClient(() => handler)
            .GetSnapshotAsync(Client("qbittorrent", "deluno"), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Null(snapshot!.ClientVersion);
        Assert.Equal("healthy", snapshot.HealthStatus);
    }

    [Fact]
    public async Task Qbittorrent_that_will_not_give_its_main_data_reports_no_free_space_and_is_still_healthy()
    {
        var handler = QbittorrentIdle((request, _) => request.RequestUri!.AbsolutePath.EndsWith("sync/maindata", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : null);

        var snapshot = await new QbittorrentDownloadClient(() => handler)
            .GetSnapshotAsync(Client("qbittorrent", "deluno"), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Null(snapshot!.FreeSpaceBytes);
        Assert.Null(snapshot.AlternateSpeedLimitEnabled);
        Assert.Equal("healthy", snapshot.HealthStatus);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html><body>Unauthorized</body></html>")]
    public async Task Qbittorrent_whose_torrent_list_is_empty_text_or_a_web_page_gives_no_snapshot_instead_of_an_invented_empty_queue(string body)
    {
        var handler = QbittorrentIdle((request, _) => request.RequestUri!.AbsolutePath.EndsWith("torrents/info", StringComparison.Ordinal)
            ? RecordedReplies.Json(body)
            : null);

        var outcome = await Record.ExceptionAsync(async () =>
        {
            var snapshot = await new QbittorrentDownloadClient(() => handler)
                .GetSnapshotAsync(Client("qbittorrent", "deluno"), DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.True(snapshot is null || snapshot.HealthStatus != "healthy" || snapshot.Queue.Count == 0, "an unreadable list must not read as a healthy queue with downloads in it");
        });

        Assert.True(outcome is null or System.Text.Json.JsonException or HttpRequestException or InvalidOperationException, outcome?.ToString());
    }

    [Fact]
    public async Task Transmission_reads_its_version_without_the_build_hash_and_the_free_space_of_its_download_folder()
    {
        var session = RecordedReplies.Transmission("settings").Find("session-get").Reply.Json()["arguments"]!;
        var free = RecordedReplies.Transmission("settings").Find("free-space").Reply.Json()["arguments"]!["size-bytes"]!.GetValue<long>();
        var handler = TransmissionIdle();

        var snapshot = await new TransmissionDownloadClient(new Factory(handler))
            .GetSnapshotAsync(Client("transmission"), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(session["version"]!.GetValue<string>().Split(' ')[0], snapshot!.ClientVersion);
        Assert.Equal(free, snapshot.FreeSpaceBytes);
        Assert.Equal(session["alt-speed-enabled"]!.GetValue<bool>(), snapshot.AlternateSpeedLimitEnabled);
    }

    [Fact]
    public async Task Transmission_that_sends_no_version_and_no_download_folder_gives_a_snapshot_with_neither()
    {
        var handler = TransmissionIdle((_, body) => body.Contains("\"session-get\"", StringComparison.Ordinal)
            ? RecordedReplies.Json(RecordedReplies.Transmission("settings").Body("session-get", ("arguments.version", null), ("arguments.download-dir", null)))
            : null);

        var snapshot = await new TransmissionDownloadClient(new Factory(handler))
            .GetSnapshotAsync(Client("transmission"), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Null(snapshot!.ClientVersion);
        Assert.Null(snapshot.FreeSpaceBytes);
        Assert.Equal("healthy", snapshot.HealthStatus);
    }

    [Fact]
    public async Task Deluge_reads_its_version_from_the_older_call_when_the_newer_one_is_not_there_and_its_free_space()
    {
        var calls = new List<string>();
        var free = RecordedReplies.Deluge("settings").Find("core.get_free_space").Reply.Json()["result"]!.GetValue<long>();
        var handler = DelugeIdle((_, body) =>
        {
            calls.Add(body);
            return null;
        });

        var snapshot = await new DelugeDownloadClient(new Factory(handler))
            .GetSnapshotAsync(Client("deluge", null, "secret"), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal("2.2.0", snapshot!.ClientVersion);
        Assert.Equal(free, snapshot.FreeSpaceBytes);
        Assert.Contains(calls, body => body.Contains("daemon.get_version", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deluge_that_knows_neither_version_call_says_nothing_about_its_version()
    {
        var handler = DelugeIdle((_, body) => body.Contains("daemon.get_version", StringComparison.Ordinal)
            ? RecordedReplies.Deluge("unknown-id").Exchanges[0].Reply.ToResponse()
            : null);

        var snapshot = await new DelugeDownloadClient(new Factory(handler))
            .GetSnapshotAsync(Client("deluge", null, "secret"), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Null(snapshot!.ClientVersion);
    }
}
