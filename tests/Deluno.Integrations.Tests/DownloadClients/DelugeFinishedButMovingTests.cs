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
        // Deluge 2.2.0's recorded list of a finished torrent with a label and a move-on-completion folder, in the state and folder under test.
        var handler = RecordedDelugeRig.Handler(RecordedDelugeRig.Finished(
            ("state", state), ("label", "movies"), ("save_path", savePath), ("move_completed", true), ("move_completed_path", @"C:\Deluno\Downloads-Complete\Deluge")));
        var snapshot = await new DelugeDownloadClient(new Factory(handler)).GetSnapshotAsync(Client(), DateTimeOffset.UtcNow, CancellationToken.None);
        return Assert.Single(snapshot!.Queue);
    }

    private static DownloadClientItem Client()
        => new("deluge-1", "Deluge", "deluge", "localhost", 8112, null, null, "http://localhost:8112/", false, null, "movies", "tv", null, 1, true,
            "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
