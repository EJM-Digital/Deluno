using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1150: Deluge reports a torrent finished before its Label plugin has moved
/// it out of the incomplete folder, and the folder Deluno would then hand to a
/// processor is the one the move is about to empty. The torrent is not ready
/// until it is saved in the folder its label moves finished downloads to.
/// </summary>
public sealed class ADelugeTorrentIsReadyOnlyInItsFinalFolderTests
{
    private const string Incomplete = @"D:\Incomplete\Deluge";
    private const string Completed = @"C:\Downloads\Completed\Movies";

    [Fact]
    public async Task A_finished_torrent_still_in_the_incomplete_folder_is_not_ready_to_hand_over()
    {
        var item = await Snapshot(savePath: Incomplete, labelMoves: true);

        Assert.Equal(DownloadQueueStatuses.Downloading, item.Status);
    }

    [Fact]
    public async Task The_same_torrent_is_ready_once_it_is_in_the_completed_folder_and_its_path_is_the_final_one()
    {
        var item = await Snapshot(savePath: Completed + @"\", labelMoves: true);

        Assert.NotEqual(DownloadQueueStatuses.Downloading, item.Status);
        Assert.Equal(DownloadQueueStatuses.ImportReady, item.Status);
        Assert.StartsWith(Completed, item.SourcePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_label_that_moves_nothing_leaves_a_finished_torrent_ready_where_it_is()
    {
        var item = await Snapshot(savePath: Incomplete, labelMoves: false);

        Assert.Equal(DownloadQueueStatuses.ImportReady, item.Status);
    }

    private static async Task<DownloadQueueItem> Snapshot(string savePath, bool labelMoves)
    {
        // Deluge 2.2.0's recorded list of a finished torrent that carries a label, saved where the test says; the label's options and
        // the core settings are the recorded ones with the move-on-completion switch (and its folder) set as the test says.
        var handler = RecordedDelugeRig.Handler(
            RecordedDelugeRig.Finished(("label", "movies"), ("save_path", savePath), ("move_completed", false), ("move_completed_path", string.Empty)),
            RecordedDelugeRig.LabelOptions(labelMoves, Completed),
            RecordedDelugeRig.CoreConfig(false, string.Empty));
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
