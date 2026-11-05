using System.Net;
using System.Text.Json.Nodes;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;
using Moq;
using static Deluno.Integrations.Tests.DownloadClients.RecordedClientCalls;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1493 and #1494, the cases where a client will not say how many downloads it runs at once: it cannot be reached, it refuses the
/// question, or it answers with a setting that says nothing usable. Then Deluno names no limit (and a full client is never guessed),
/// and the rest of what the client reports is still read. Every reply that is not a failure is the client's own recording, changed
/// only in the one value the test is about (each test says which).
/// </summary>
public sealed class AnActiveDownloadLimitTheClientWillNotSayIsNeverGuessedTests
{
    private static DownloadClientItem NoAddress(string protocol) => Client(protocol) with { Host = string.Empty, EndpointUrl = null };

    // ------------------------------------------------------------------ the interface's own answer

    [Fact]
    public async Task A_client_with_nothing_to_report_about_its_load_reports_nothing_by_default()
    {
        var client = new Mock<IDownloadClient> { CallBase = true }.Object;

        Assert.Null(await client.GetLoadAsync(Client("sabnzbd"), CancellationToken.None));
    }

    // ------------------------------------------------------------------ qBittorrent

    private static async Task<DownloadClientLoad?> QbittorrentLoad(Func<HttpRequestMessage, string, HttpResponseMessage?> answer)
    {
        var calls = new Calls("qbittorrent", [RecordedReplies.Qbittorrent("sign-in"), RecordedReplies.Qbittorrent("version")], answer);
        return await new QbittorrentDownloadClient(() => calls.Build()).GetLoadAsync(Client("qbittorrent", "deluno"), CancellationToken.None);
    }

    [Fact]
    public async Task Qbittorrent_with_no_address_reports_no_load()
        => Assert.Null(await new QbittorrentDownloadClient(() => RecordedReplies.Handler("qbittorrent", [])).GetLoadAsync(NoAddress("qbittorrent"), CancellationToken.None));

    [Fact]
    public async Task Qbittorrent_refusing_the_preferences_reports_no_load()
        => Assert.Null(await QbittorrentLoad((request, _) => request.RequestUri!.AbsolutePath.EndsWith("app/preferences", StringComparison.Ordinal)
            ? RecordedReplies.Json("{}", HttpStatusCode.Forbidden) : null));

    [Fact]
    public async Task Qbittorrent_whose_list_is_empty_or_has_no_state_counts_nothing_running_against_its_recorded_limit()
    {
        // The recorded preferences (queueing on, 5 active downloads); the torrent list is changed to null, then to a torrent with no state.
        var preferences = RecordedReplies.Qbittorrent("settings").Body("app/preferences");
        foreach (var list in new[] { "null", "[{\"hash\":\"abc\",\"name\":\"x\"}]" })
        {
            var load = await QbittorrentLoad((request, _) => request.RequestUri!.AbsolutePath.EndsWith("app/preferences", StringComparison.Ordinal) ? RecordedReplies.Json(preferences)
                : request.RequestUri.AbsolutePath.EndsWith("torrents/info", StringComparison.Ordinal) ? RecordedReplies.Json(list)
                : null);

            Assert.Equal(new DownloadClientLoad(0, 5), load);
        }
    }

    [Fact]
    public async Task Qbittorrent_not_answering_the_preferences_in_a_snapshot_names_no_limit_but_still_lists_its_downloads()
    {
        var handler = RecordedQbittorrentRig.Handler(
            RecordedReplies.Qbittorrent("queued").Body("torrents/info"),
            intercept: (request, _) => request.RequestUri!.AbsolutePath.EndsWith("app/preferences", StringComparison.Ordinal)
                ? throw new HttpRequestException("connection reset")
                : null);

        var snapshot = (await new QbittorrentDownloadClient(() => handler).GetSnapshotAsync(Client("qbittorrent", "deluno"), DateTimeOffset.UtcNow, CancellationToken.None))!;

        Assert.Null(snapshot.ActiveDownloadLimit);
        Assert.Single(snapshot.Queue);
    }

    // ------------------------------------------------------------------ Transmission

    [Fact]
    public async Task Transmission_with_no_address_reports_no_load()
        => Assert.Null(await new TransmissionDownloadClient(new Factory(RecordedReplies.Handler("transmission", []))).GetLoadAsync(NoAddress("transmission"), CancellationToken.None));

    [Fact]
    public async Task Transmission_with_its_download_queue_off_names_no_limit_in_a_snapshot()
    {
        // The recorded session-get reply with download-queue-enabled turned off.
        var session = RecordedReplies.Transmission("settings").Body("session-get", ("arguments.download-queue-enabled", false));
        var handler = RecordedTransmissionRig.Handler(RecordedTransmissionRig.Reply("queued"),
            (_, body) => body.Contains("session-get", StringComparison.Ordinal) ? RecordedReplies.Json(session) : null);

        var snapshot = (await new TransmissionDownloadClient(new Factory(handler)).GetSnapshotAsync(Client("transmission"), DateTimeOffset.UtcNow, CancellationToken.None))!;

        Assert.Null(snapshot.ActiveDownloadLimit);
    }

    [Fact]
    public async Task Transmission_whose_session_reply_carries_no_settings_names_no_limit_in_a_snapshot()
    {
        // The recorded session-get reply with its arguments removed: Transmission answered, but with nothing in it.
        var session = RecordedReplies.Transmission("settings").Body("session-get", ("arguments", null));
        var handler = RecordedTransmissionRig.Handler(RecordedTransmissionRig.Reply("queued"),
            (_, body) => body.Contains("session-get", StringComparison.Ordinal) ? RecordedReplies.Json(session) : null);

        var snapshot = (await new TransmissionDownloadClient(new Factory(handler)).GetSnapshotAsync(Client("transmission"), DateTimeOffset.UtcNow, CancellationToken.None))!;

        Assert.Null(snapshot.ActiveDownloadLimit);
    }

    // ------------------------------------------------------------------ Deluge

    [Fact]
    public async Task Deluge_with_no_address_reports_no_load()
        => Assert.Null(await new DelugeDownloadClient(new Factory(RecordedReplies.Handler("deluge", []))).GetLoadAsync(NoAddress("deluge"), CancellationToken.None));

    [Theory]
    [InlineData("settingNotAnObject")]
    [InlineData("otherKeyOnly")]
    [InlineData("limitNotANumber")]
    [InlineData("limitNotAWholeNumber")]
    [InlineData("noReplyBody")]
    public async Task Deluge_whose_setting_says_nothing_usable_has_no_limit(string shape)
    {
        // The recorded core.get_config_values reply, with its result changed to the shape under test.
        object? result = shape switch
        {
            "settingNotAnObject" => "none",
            "otherKeyOnly" => new Dictionary<string, object?> { ["max_active_seeding"] = 3 },
            "limitNotAWholeNumber" => new Dictionary<string, object?> { ["max_active_downloading"] = 2.5 },
            _ => new Dictionary<string, object?> { ["max_active_downloading"] = "many" },
        };
        var config = shape == "noReplyBody" ? "null" : RecordedReplies.Deluge("settings").Body("core.get_config_values", ("result", result));
        var handler = RecordedDelugeRig.Handler(RecordedDelugeRig.Downloading(), coreConfig: config);

        var load = await new DelugeDownloadClient(new Factory(handler)).GetLoadAsync(Client("deluge"), CancellationToken.None);

        Assert.Equal(new DownloadClientLoad(0, null), load);
    }

    // ------------------------------------------------------------------ uTorrent

    private static async Task<DownloadClientLoad?> UtorrentSnapshotLimit(Func<HttpResponseMessage> settings)
    {
        var list = RecordedReplies.Utorrent("queued").Exchanges.Last(e => e.Request.Path.Contains("list=1", StringComparison.Ordinal)).Reply.Body!;
        var handler = new DelegateHandler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith("token.html", StringComparison.Ordinal)) return RecordedReplies.Json(RecordedReplies.UtorrentTokenPage());
            if (uri.Query.Contains("list=1", StringComparison.Ordinal)) return RecordedReplies.Json(list);
            if (uri.Query.Contains("action=getsettings", StringComparison.Ordinal)) return settings();
            return RecordedReplies.Json("{}");
        });
        var snapshot = (await new UTorrentDownloadClient(() => handler).GetSnapshotAsync(Client("utorrent"), DateTimeOffset.UtcNow, CancellationToken.None))!;
        return new DownloadClientLoad(snapshot.Queue.Count, snapshot.ActiveDownloadLimit);
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }

    private static string RecordedSettingsWith(string limit)
    {
        // uTorrent's recorded getsettings reply, with the value of max_active_downloads set to the one under test.
        var root = JsonNode.Parse(RecordedReplies.Utorrent("settings").Single.Reply.Body!)!;
        var row = root["settings"]!.AsArray().Single(r => r![0]!.GetValue<string>() == "max_active_downloads")!;
        row[2] = limit;
        return root.ToJsonString();
    }

    // uTorrent's recorded getsettings reply with the limit row removed, or with no settings list at all (a null row).
    private static string RecordedSettingsWithout(string? row)
    {
        var root = JsonNode.Parse(RecordedReplies.Utorrent("settings").Single.Reply.Body!)!.AsObject();
        if (row is null)
        {
            root.Remove("settings");
            return root.ToJsonString();
        }

        var rows = root["settings"]!.AsArray();
        rows.Remove(rows.Single(r => r![0]!.GetValue<string>() == row));
        return root.ToJsonString();
    }

    [Fact]
    public async Task A_recorded_uTorrent_limit_is_named_and_a_limit_of_zero_means_none()
    {
        Assert.Equal(5, (await UtorrentSnapshotLimit(() => RecordedReplies.Json(RecordedSettingsWith("5"))))!.Limit);
        Assert.Null((await UtorrentSnapshotLimit(() => RecordedReplies.Json(RecordedSettingsWith("0"))))!.Limit);
    }

    [Theory]
    [InlineData("refused")]
    [InlineData("noSettingsList")]
    [InlineData("noRowForTheLimit")]
    [InlineData("notJson")]
    public async Task A_uTorrent_that_will_not_say_its_limit_names_none_and_still_lists_its_downloads(string failure)
    {
        var load = await UtorrentSnapshotLimit(() => failure switch
        {
            "refused" => RecordedReplies.Json("{}", HttpStatusCode.InternalServerError),
            "noSettingsList" => RecordedReplies.Json(RecordedSettingsWithout(null)),
            "noRowForTheLimit" => RecordedReplies.Json(RecordedSettingsWithout("max_active_downloads")),
            _ => RecordedReplies.Json("<html>"),
        });

        Assert.Null(load!.Limit);
        Assert.True(load.Active > 0);
    }
}
