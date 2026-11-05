using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;
using static Deluno.Integrations.Tests.DownloadClients.RecordedClientCalls;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1494: each torrent client's active-download limit and active count, read from that client's own recorded replies
/// (tests/fixtures/recorded). The settings are the recorded ones with only the limit changed, and the torrent lists are
/// recorded lists. Where a value was never recorded the test says what was changed.
/// </summary>
public sealed class DownloadClientLoadOnRecordingsTests
{
    // qBittorrent 5.2.1: its recorded preferences (queueing on, 5 active downloads) and its recorded list of four torrents:
    // one stalledDL, one queuedDL and two stalledUP, so one is actively downloading.
    private static async Task<DownloadClientLoad?> Qbittorrent(params (string Path, object? Value)[] preferenceEdits)
    {
        var preferences = RecordedReplies.Qbittorrent("settings").Body("app/preferences", preferenceEdits);
        var list = RecordedReplies.Qbittorrent("queued").Exchanges.Single(e => e.Request.Path == "/api/v2/torrents/info").Reply.Body!;
        var calls = new Calls("qbittorrent", [RecordedReplies.Qbittorrent("sign-in"), RecordedReplies.Qbittorrent("version")], (request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("app/preferences", StringComparison.Ordinal) ? RecordedReplies.Json(preferences)
            : request.RequestUri.AbsolutePath.EndsWith("torrents/info", StringComparison.Ordinal) ? RecordedReplies.Json(list)
            : null);
        return await new QbittorrentDownloadClient(() => calls.Build()).GetLoadAsync(Client("qbittorrent", "deluno"), CancellationToken.None);
    }

    [Fact]
    public async Task Qbittorrent_with_queueing_on_reports_its_limit_and_counts_the_downloads_running()
        => Assert.Equal(new DownloadClientLoad(1, 5), await Qbittorrent());

    [Fact]
    public async Task Qbittorrent_at_its_limit_is_full_and_a_queued_download_does_not_count_as_running()
    {
        var load = await Qbittorrent(("max_active_downloads", 1));

        Assert.Equal(new DownloadClientLoad(1, 1), load);
        Assert.True(load!.IsFull);
    }

    [Fact]
    public async Task Qbittorrent_with_queueing_off_has_no_limit_whatever_the_number_says()
        => Assert.Equal(new DownloadClientLoad(0, null), await Qbittorrent(("queueing_enabled", false), ("max_active_downloads", 1)));

    [Fact]
    public async Task Qbittorrent_with_minus_one_or_zero_active_downloads_has_no_limit()
    {
        Assert.Null((await Qbittorrent(("max_active_downloads", -1)))!.Limit);
        Assert.Null((await Qbittorrent(("max_active_downloads", 0)))!.Limit);
    }

    // Transmission 4.1.3: its recorded settings (download-queue-enabled, size 5) and its recorded list of eleven torrents:
    // nine seeding (6), one downloading (4) and one waiting for a slot (3).
    private static async Task<DownloadClientLoad?> Transmission(params (string Path, object? Value)[] settingEdits)
    {
        var session = RecordedReplies.Transmission("settings").Body("session-get", settingEdits.Select(e => ("arguments." + e.Path, e.Value)).ToArray());
        var list = RecordedReplies.Transmission("queued").Exchanges.Single(e => e.Reply.Body is { } body && body.Replace(" ", string.Empty).Contains("\"status\":4", StringComparison.Ordinal)).Reply.Body!;
        var calls = new Calls("transmission", [RecordedReplies.Transmission("settings")], (_, body) =>
            body.Contains("session-get", StringComparison.Ordinal) ? RecordedReplies.Json(session)
            : body.Contains("torrent-get", StringComparison.Ordinal) ? RecordedReplies.Json(list)
            : null);
        return await new TransmissionDownloadClient(new Factory(calls.Build())).GetLoadAsync(Client("transmission"), CancellationToken.None);
    }

    [Fact]
    public async Task Transmission_with_its_download_queue_on_reports_the_queue_size_and_counts_only_downloading_torrents()
        => Assert.Equal(new DownloadClientLoad(1, 5), await Transmission());

    [Fact]
    public async Task Transmission_at_its_queue_size_is_full_and_a_torrent_waiting_for_a_slot_does_not_count()
        => Assert.True((await Transmission(("download-queue-size", 1)))!.IsFull);

    [Fact]
    public async Task Transmission_with_its_download_queue_off_has_no_limit()
        => Assert.Equal(new DownloadClientLoad(0, null), await Transmission(("download-queue-enabled", false), ("download-queue-size", 1)));

    // Deluge 2.2.0: max_active_downloading was never recorded. The reply is the recorded core.get_config_values reply
    // (tests/fixtures/recorded/deluge/2.2.0/settings.json) with that one value as its result; see MISSING.md.
    // The torrent lists are recorded.
    private static async Task<DownloadClientLoad?> Deluge(int limit, string updateUi)
    {
        var config = RecordedReplies.Deluge("settings").Body("core.get_config_values", ("result", new Dictionary<string, object?> { ["max_active_downloading"] = limit }));
        var handler = RecordedDelugeRig.Handler(updateUi, coreConfig: config);
        return await new DelugeDownloadClient(new Factory(handler)).GetLoadAsync(Client("deluge"), CancellationToken.None);
    }

    [Fact]
    public async Task Deluge_reports_its_limit_and_counts_a_downloading_torrent()
        => Assert.Equal(new DownloadClientLoad(1, 3), await Deluge(3, RecordedDelugeRig.Downloading()));

    [Fact]
    public async Task Deluge_at_its_limit_is_full()
        => Assert.True((await Deluge(1, RecordedDelugeRig.Downloading()))!.IsFull);

    [Fact]
    public async Task A_Deluge_torrent_waiting_in_its_queue_does_not_count_as_running()
        => Assert.Equal(new DownloadClientLoad(0, 1), await Deluge(1, RecordedReplies.Deluge("queued").Body("web.update_ui")));

    [Fact]
    public async Task Deluge_with_minus_one_has_no_limit()
        => Assert.Null((await Deluge(-1, RecordedDelugeRig.Downloading()))!.Limit);

    [Fact]
    public async Task A_client_with_no_active_limit_to_read_reports_nothing()
    {
        var factory = new Factory(RecordedReplies.Handler("sabnzbd", []));
        IDownloadClient[] clients = [new SabnzbdDownloadClient(factory), new NzbGetDownloadClient(factory), new UTorrentDownloadClient(), new RtorrentDownloadClient(factory)];

        foreach (var client in clients)
        {
            Assert.Null(await client.GetLoadAsync(Client(client.Protocol), CancellationToken.None));
        }
    }
}
