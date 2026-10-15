using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;
using static Deluno.Integrations.Tests.DownloadClients.RecordedClientCalls;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1301: the actions Deluno sends to a torrent client that are recorded from the real client (a re-check, and a queue
/// move to the top), and what each client then says of a torrent whose data is gone (the recorded "failed" state: the file
/// was removed behind the client's back, then re-checked).
/// </summary>
public sealed class TorrentClientActionsOnRecordingsTests
{
    private const string QbitHash = "2b989b4d477b273025b90f3743d314e92d11a8fa";
    private const string TransmissionHash = "937e80abc22fab1cd6d130e04c63d6a3fef582ae";
    private const string DelugeHash = "139422fdc923bb71e01c87542e8f517a07240837";

    [Fact]
    public async Task A_recheck_of_a_qbittorrent_torrent_posts_torrents_recheck_with_its_hash_and_is_done()
    {
        var calls = new Calls("qbittorrent", [RecordedReplies.Qbittorrent("sign-in"), RecordedReplies.Qbittorrent("version"), RecordedReplies.Qbittorrent("failed")]);
        var handler = calls.Build();

        var result = await new QbittorrentDownloadClient(() => handler)
            .ExecuteActionAsync(Client("qbittorrent", "deluno"), DownloadClientActions.Recheck, QbitHash, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(calls.Sent("POST /api/v2/torrents/recheck"));
        Assert.True(calls.Sent("hashes=" + QbitHash));
    }

    [Fact]
    public async Task A_recheck_of_a_transmission_torrent_sends_torrent_verify_for_its_hash_and_is_done()
    {
        var calls = new Calls("transmission", [RecordedReplies.Transmission("session-handshake"), RecordedReplies.Transmission("failed")]);
        var handler = calls.Build();

        var result = await new TransmissionDownloadClient(new Factory(handler))
            .ExecuteActionAsync(Client("transmission"), DownloadClientActions.Recheck, TransmissionHash, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(calls.Sent("\"torrent-verify\""));
        Assert.True(calls.Sent(TransmissionHash));
    }

    [Fact]
    public async Task A_recheck_of_a_deluge_torrent_calls_core_force_recheck_with_its_hash_and_is_done()
    {
        var calls = new Calls("deluge", [RecordedReplies.Deluge("sign-in"), RecordedReplies.Deluge("failed")]);
        var handler = calls.Build();

        var result = await new DelugeDownloadClient(new Factory(handler))
            .ExecuteActionAsync(Client("deluge", null, "secret"), DownloadClientActions.Recheck, DelugeHash, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(calls.Sent("core.force_recheck"));
        Assert.True(calls.Sent(DelugeHash));
    }

    [Fact]
    public async Task A_transmission_move_to_the_top_sends_queue_move_top_for_the_torrents_hash()
    {
        var calls = new Calls("transmission", [RecordedReplies.Transmission("session-handshake"), RecordedReplies.Transmission("queue-moved"), RecordedReplies.Transmission("finished-single-file")]);
        var handler = calls.Build();

        var result = await new TransmissionDownloadClient(new Factory(handler))
            .ExecuteActionAsync(Client("transmission"), DownloadClientActions.MoveToTop, TransmissionHash, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(calls.Sent("\"queue-move-top\""));
    }

    [Fact]
    public async Task A_qbittorrent_5_torrent_a_person_stopped_reads_as_paused_by_its_stopped_state_name()
    {
        var info = RecordedReplies.Qbittorrent("paused").Body("torrents/info");
        var handler = RecordedQbittorrentRig.Handler(info);

        var snapshot = await new QbittorrentDownloadClient(() => handler)
            .GetSnapshotAsync(Client("qbittorrent", "deluno"), DateTimeOffset.UtcNow, CancellationToken.None);

        var recorded = System.Text.Json.Nodes.JsonNode.Parse(info)!.AsArray()[0]!["state"]!.GetValue<string>();
        Assert.StartsWith("stopped", recorded, StringComparison.Ordinal);
        Assert.Contains(snapshot!.Queue, item => item.Status == DownloadQueueStatuses.Paused);
    }

    [Fact]
    public async Task A_qbittorrent_5_add_answered_with_the_json_object_names_the_torrent_it_took()
    {
        var added = System.Text.Json.Nodes.JsonNode.Parse(RecordedReplies.Qbittorrent("finished-single-file").Find("torrents/add").Reply.Body!)!;
        var hash = added["added_torrent_ids"]![0]!.GetValue<string>();
        var calls = new Calls("qbittorrent", [RecordedReplies.Qbittorrent("sign-in"), RecordedReplies.Qbittorrent("version"), RecordedReplies.Qbittorrent("finished-single-file")],
            (request, _) => request.RequestUri!.AbsolutePath.EndsWith("torrents/info", StringComparison.Ordinal)
                ? RecordedReplies.Json(request.RequestUri.Query.Contains("hashes", StringComparison.Ordinal)
                    ? RecordedReplies.Qbittorrent("finished-single-file").Body("torrents/info")
                    : RecordedReplies.Qbittorrent("idle").Body("torrents/info", ("^", 0)))
                : null);
        var handler = calls.Build();
        var request = new DownloadClientGrabRequest("Example.Movie.2026.1080p", "magnet:?xt=urn:btih:" + hash + "&dn=Example", "movies", "movies", "Example Indexer");

        var result = await new QbittorrentDownloadClient(() => handler).GrabAsync(Client("qbittorrent", "deluno"), request, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(hash, result.ExternalId);
        Assert.True(calls.Sent("urls=magnet"), "a link is sent as urls");
    }

    [Fact]
    public async Task A_qbittorrent_torrent_whose_data_a_recheck_could_not_find_is_listed_at_nothing_downloaded_and_never_as_finished()
    {
        var info = RecordedReplies.Qbittorrent("failed").Body("torrents/info");
        var handler = RecordedQbittorrentRig.Handler(info);

        var snapshot = await new QbittorrentDownloadClient(() => handler)
            .GetSnapshotAsync(Client("qbittorrent", "deluno"), DateTimeOffset.UtcNow, CancellationToken.None);

        var item = Assert.Single(snapshot!.Queue);
        Assert.Equal(0d, item.Progress);
        Assert.NotEqual(DownloadQueueStatuses.ImportReady, item.Status);
        Assert.True(item.Status is DownloadQueueStatuses.Downloading or DownloadQueueStatuses.Stalled, item.Status);
    }
}
