using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// Live on the rig (#606): Deluge reached 100% in its incomplete folder, Deluno
/// handed Weir that path, and Weir refused it. Deluge moves a finished torrent
/// to its completed folder afterwards, so until it has, the download is not
/// ready.
/// </summary>
public sealed class DelugeFinishedButMovingTests
{
    [Fact]
    public async Task A_finished_torrent_still_in_the_incomplete_folder_is_not_ready()
    {
        var item = await Snapshot(state: "Seeding", savePath: @"C:\Deluno\Downloads-Incomplete\Deluge");

        Assert.Equal(DownloadQueueStatuses.Downloading, item.Status);
    }

    [Fact]
    public async Task A_torrent_being_moved_is_not_ready()
        => Assert.Equal(DownloadQueueStatuses.Downloading, (await Snapshot(state: "Moving", savePath: @"C:\Deluno\Downloads-Complete\Deluge")).Status);

    /// <summary>
    /// #912: Deluge re-checking an already-complete torrent (a restart, a
    /// moved data folder) reports its own "Checking" state at whatever
    /// progress it already reached, including 100. That must never read as
    /// ready to import — Deluge could still rewrite the file underneath an
    /// import that raced ahead of the check.
    /// </summary>
    [Fact]
    public async Task A_torrent_being_rechecked_is_not_ready_even_at_full_progress()
        => Assert.Equal(DownloadQueueStatuses.NotTransferring, (await Snapshot(state: "Checking", savePath: @"C:\Deluno\Downloads-Complete\Deluge")).Status);

    [Fact]
    public async Task Once_moved_it_is_ready_where_it_now_is()
    {
        var item = await Snapshot(state: "Seeding", savePath: @"C:\Deluno\Downloads-Complete\Deluge\");

        Assert.NotEqual(DownloadQueueStatuses.Downloading, item.Status);
        Assert.StartsWith(@"C:\Deluno\Downloads-Complete\Deluge", item.SourcePath ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<DownloadQueueItem> Snapshot(string state, string savePath)
    {
        var torrent = "{\"result\":{\"torrents\":{\"abc123\":{\"name\":\"House.on.Haunted.Hill.1959.1080p.BluRay.x264-AMIABLE\",\"state\":" + Json(state)
            + ",\"progress\":100.0,\"download_payload_rate\":0,\"upload_payload_rate\":0,\"eta\":0,\"total_size\":5000000000,\"total_done\":5000000000,\"num_peers\":0,"
            + "\"time_added\":1727000000,\"label\":\"movies\",\"message\":\"OK\",\"save_path\":" + Json(savePath)
            + ",\"move_completed\":true,\"move_completed_path\":" + Json(@"C:\Deluno\Downloads-Complete\Deluge") + "}}},\"error\":null,\"id\":2}";
        var handler = new DelugeHandler(torrent);
        var snapshot = await new DelugeDownloadClient(new Factory(handler)).GetSnapshotAsync(Client(), DateTimeOffset.UtcNow, CancellationToken.None);
        return Assert.Single(snapshot!.Queue);
    }

    private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static DownloadClientItem Client()
        => new("deluge-1", "Deluge", "deluge", "localhost", 8112, null, null, "http://localhost:8112/", false, null, "movies", "tv", null, 1, true,
            "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class DelugeHandler(string updateUi) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var answer = body.Contains("web.update_ui", StringComparison.Ordinal) ? updateUi : """{"result":true,"error":null,"id":1}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }
}
