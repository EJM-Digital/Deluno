using System.Net;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1026: Transmission and Deluge report a queue place and per-torrent seed
/// limits, and the queue actions from #810 reach each client's own calls.
/// Transmission: <c>queuePosition</c> (from 0), <c>seedRatioMode</c> /
/// <c>seedRatioLimit</c> and <c>seedIdleMode</c> / <c>seedIdleLimit</c>
/// (minutes), where mode 0 follows the daemon, 1 uses the torrent's own limit
/// and 2 means no limit; move calls are <c>queue-move-top/up/down/bottom</c>.
/// Deluge: <c>queue</c> (from 0, -1 when not queued), <c>stop_at_ratio</c> /
/// <c>stop_ratio</c> / <c>remove_at_ratio</c>; move calls are
/// <c>core.queue_top/up/down/bottom</c>.
/// </summary>
public sealed class TransmissionAndDelugeQueueAndSeedLimitTests
{
    // ----------------------------------------------------------- Transmission

    [Fact]
    public async Task Transmission_asks_for_the_queue_and_seed_fields_and_counts_the_queue_from_one()
    {
        var sent = new List<string>();
        var item = await TransmissionSnapshot(sent, ("queuePosition", 0), ("seedRatioMode", 1), ("seedRatioLimit", 2.0), ("seedIdleMode", 0), ("seedIdleLimit", 30));

        Assert.Equal(1, item.QueuePosition);
        var torrentGet = Assert.Single(sent, body => body.Contains("\"torrent-get\"", StringComparison.Ordinal));
        foreach (var field in new[] { "queuePosition", "seedRatioMode", "seedRatioLimit", "seedIdleMode", "seedIdleLimit" })
        {
            Assert.Contains($"\"{field}\"", torrentGet, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(1, 2.0, 1, 30, "Stops seeding at ratio 2 or after 30 minutes with no uploading.")]
    [InlineData(1, 1.5, 0, 30, "Stops seeding at ratio 1.5.")]
    [InlineData(0, 2.0, 1, 1, "Stops seeding at 1 minute with no uploading.")]
    [InlineData(0, 2.0, 0, 30, "Follows Transmission's overall seed limits.")]
    [InlineData(2, 2.0, 2, 30, "Seeds with no limit.")]
    [InlineData(2, 2.0, 0, 30, "Follows Transmission's overall seed limits.")]
    public async Task Transmission_seed_limits_are_read_from_each_torrents_own_mode(int ratioMode, double ratio, int idleMode, int idle, string expected)
    {
        var item = await TransmissionSnapshot(
            [], ("queuePosition", 4), ("seedRatioMode", ratioMode), ("seedRatioLimit", ratio), ("seedIdleMode", idleMode), ("seedIdleLimit", idle));

        Assert.Equal(5, item.QueuePosition);
        Assert.Equal(expected, item.SeedLimit);
    }

    [Fact]
    public async Task Transmission_reports_neither_when_the_daemon_sends_neither()
    {
        // The recorded torrent, whose field list had neither the queue place nor the seed limits.
        var item = await TransmissionSnapshot([], ("queuePosition", null), ("seedRatioMode", null), ("seedRatioLimit", null), ("seedIdleMode", null), ("seedIdleLimit", null));

        Assert.Null(item.QueuePosition);
        Assert.Null(item.SeedLimit);
    }

    [Theory]
    [InlineData("move-to-top", "queue-move-top")]
    [InlineData("move-up", "queue-move-up")]
    [InlineData("move-down", "queue-move-down")]
    [InlineData("move-to-bottom", "queue-move-bottom")]
    public async Task Transmission_reorder_actions_call_its_own_queue_move_methods_on_that_torrent(string action, string method)
    {
        var sent = new List<string>();
        var result = await new TransmissionDownloadClient(new Factory(new RecordingHandler(sent, RecordedReplies.Transmission("queue-moved").Find("queue-move-top").Reply.Body!)))
            .ExecuteActionAsync(TransmissionClient(), action, "7", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        using var document = JsonDocument.Parse(Assert.Single(sent));
        Assert.Equal(method, document.RootElement.GetProperty("method").GetString());
        Assert.Equal(7, document.RootElement.GetProperty("arguments").GetProperty("ids")[0].GetInt32());
    }

    // ------------------------------------------------------------------ Deluge

    [Fact]
    public async Task Deluge_asks_for_the_queue_and_seed_fields()
    {
        var sent = new List<string>();
        await DelugeSnapshot(sent, ("queue", 0), ("stop_at_ratio", true), ("stop_ratio", 2.0), ("remove_at_ratio", false));

        var update = Assert.Single(sent, body => body.Contains("web.update_ui", StringComparison.Ordinal));
        foreach (var field in new[] { "queue", "stop_at_ratio", "stop_ratio", "remove_at_ratio" })
        {
            Assert.Contains($"\"{field}\"", update, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(6, 7)]
    public async Task Deluge_counts_its_queue_from_one(int queue, int expected)
    {
        var item = await DelugeSnapshot([], ("queue", queue));

        Assert.Equal(expected, item.QueuePosition);
    }

    [Fact]
    public async Task Deluge_reads_minus_one_as_not_queued()
    {
        var item = await DelugeSnapshot([], ("queue", -1));

        Assert.Null(item.QueuePosition);
    }

    [Theory]
    [InlineData(true, 2.0, false, "Stops seeding at ratio 2.")]
    [InlineData(true, 1.25, true, "Removed from Deluge at ratio 1.25.")]
    [InlineData(false, 2.0, false, "Seeds with no ratio limit.")]
    public async Task Deluge_seed_limit_is_the_torrents_own_stop_at_ratio(bool stopAtRatio, double ratio, bool removeAtRatio, string expected)
    {
        var item = await DelugeSnapshot([], ("queue", 2), ("stop_at_ratio", stopAtRatio), ("stop_ratio", ratio), ("remove_at_ratio", removeAtRatio));

        Assert.Equal(expected, item.SeedLimit);
    }

    [Fact]
    public async Task Deluge_reports_no_seed_limit_when_it_sends_no_flag()
    {
        // The recorded torrent with the queue place and the seed flags taken out.
        var item = await DelugeSnapshot([], ("queue", null), ("stop_at_ratio", null), ("stop_ratio", null), ("remove_at_ratio", null));

        Assert.Null(item.SeedLimit);
        Assert.Null(item.QueuePosition);
    }

    [Theory]
    [InlineData("move-to-top", "core.queue_top")]
    [InlineData("move-up", "core.queue_up")]
    [InlineData("move-down", "core.queue_down")]
    [InlineData("move-to-bottom", "core.queue_bottom")]
    public async Task Deluge_reorder_actions_call_its_own_queue_methods_on_that_torrent(string action, string method)
    {
        var sent = new List<string>();
        const string hash = "0123456789abcdef0123456789abcdef01234567";
        var result = await new DelugeDownloadClient(new Factory(new DelugeQueueHandler(sent)))
            .ExecuteActionAsync(DelugeClient(), action, hash, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var call = Assert.Single(sent, body => body.Contains("queue_", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(call);
        Assert.Equal(method, document.RootElement.GetProperty("method").GetString());
        Assert.Equal(hash, document.RootElement.GetProperty("params")[0][0].GetString());
    }

    [Fact]
    public void Both_clients_say_their_queue_can_be_reordered()
    {
        Assert.True(new TransmissionDownloadClient(new Factory(new RecordingHandler([], "{}"))).Capabilities.SupportsQueueReorder);
        Assert.True(new DelugeDownloadClient(new Factory(new RecordingHandler([], "{}"))).Capabilities.SupportsQueueReorder);
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>Transmission 4.1.3's recorded list of a finished torrent, with the queue place and seed limits under test.</summary>
    private static async Task<DownloadQueueItem> TransmissionSnapshot(List<string> sent, params (string Field, object? Value)[] edits)
    {
        var torrentGet = RecordedTransmissionRig.Reply(RecordedTransmissionRig.Finished, edits);
        var snapshot = await new TransmissionDownloadClient(new Factory(RecordedTransmissionRig.Handler(torrentGet, (_, body) =>
            {
                sent.Add(body);
                return null;
            })))
            .GetSnapshotAsync(TransmissionClient(), DateTimeOffset.UtcNow, CancellationToken.None);
        return Assert.Single(snapshot!.Queue);
    }

    /// <summary>Deluge 2.2.0's recorded list of a finished torrent, with the queue place and seed flags under test.</summary>
    private static async Task<DownloadQueueItem> DelugeSnapshot(List<string> sent, params (string Field, object? Value)[] edits)
    {
        var updateUi = RecordedDelugeRig.Finished(edits.Append(("label", "movies")).ToArray());
        var snapshot = await new DelugeDownloadClient(new Factory(RecordedDelugeRig.Handler(updateUi, onCall: sent.Add)))
            .GetSnapshotAsync(DelugeClient(), DateTimeOffset.UtcNow, CancellationToken.None);
        return Assert.Single(snapshot!.Queue);
    }

    private static DownloadClientItem TransmissionClient()
        => new("transmission-1", "Transmission", "transmission", "localhost", 9091, null, null, "http://localhost:9091/", false, null, "movies", "tv", null, 1, true,
            "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static DownloadClientItem DelugeClient()
        => new("deluge-1", "Deluge", "deluge", "localhost", 8112, null, null, "http://localhost:8112/", false, null, "movies", "tv", null, 1, true,
            "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage Ok(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    /// <summary>
    /// Deluge 2.2.0 answering its sign-in with the recorded one and any queue move with the reply it recorded for a move to the top
    /// (which says nothing: a null result and no error), writing down every request body.
    /// </summary>
    private sealed class DelugeQueueHandler(List<string> sent) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            sent.Add(body);
            return body.Contains("auth.login", StringComparison.Ordinal) ? RecordedReplies.Deluge("sign-in").Response("auth.login")
                : body.Contains("web.connected", StringComparison.Ordinal) ? RecordedReplies.Deluge("sign-in").Response("web.connected")
                : RecordedReplies.Deluge("queue-moved").Response("core.queue_top");
        }
    }

    /// <summary>Writes down every request body and answers each with the same reply.</summary>
    private sealed class RecordingHandler(List<string> sent, string reply) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            sent.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return Ok(reply);
        }
    }
}
