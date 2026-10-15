using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// qBittorrent's <c>metaDL</c> (fetching metadata, no peers yet) and
/// <c>stalledDL</c> (gave up finding peers) are the same case for Deluno
/// (#650): no metadata ever arriving. <c>stalledDL</c> already reads as
/// stalled immediately through its status text; <c>metaDL</c> must reach the
/// same place once it has gone unmetadata'd for the stall window #618 gave
/// Deluge, rather than sitting as "downloading" for ever.
/// </summary>
public sealed class QbittorrentMetadataStallTests
{
    [Fact]
    public async Task StalledDL_alone_is_not_read_as_stalled()
    {
        var addedUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        var item = await RecordedSnapshot("stalledDL", size: 0, progress: 0, addedUtc, capturedUtc: addedUtc.AddMinutes(1));

        // #1193: the client's own flag is not proof; Deluno's no-progress window decides.
        Assert.Equal(DownloadQueueStatuses.Downloading, item.Status);
    }

    [Fact]
    public async Task MetaDL_is_downloading_before_the_stall_window()
    {
        var addedUtc = DateTimeOffset.UtcNow.AddMinutes(-5);
        var item = await RecordedSnapshot("metaDL", size: 0, progress: 0, addedUtc, capturedUtc: addedUtc.AddMinutes(5));

        Assert.Equal(DownloadQueueStatuses.Downloading, item.Status);
    }

    [Fact]
    public async Task MetaDL_becomes_stalled_once_it_has_gone_unmetadatad_past_the_stall_window()
    {
        var addedUtc = DateTimeOffset.UtcNow.AddMinutes(-40);
        var item = await RecordedSnapshot("metaDL", size: 0, progress: 0, addedUtc, capturedUtc: addedUtc.AddMinutes(40));

        Assert.Equal(DownloadQueueStatuses.Stalled, item.Status);
    }

    [Fact]
    public async Task Completion_is_never_inferred_from_zero_size_alone()
    {
        var addedUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        var item = await RecordedSnapshot("downloading", size: 0, progress: 1, addedUtc, capturedUtc: addedUtc.AddMinutes(1));

        Assert.NotEqual(DownloadQueueStatuses.ImportReady, item.Status);
    }

    /// <summary>
    /// #951: the magnet-metadata rule needs its own signal, distinct from the
    /// generic "stalled" status the reconciler above already gives metaDL
    /// once it has run past the no-metadata-and-no-peers window - a magnet
    /// with connected peers but still no resolved size is otherwise
    /// invisible to any rule.
    /// </summary>
    [Fact]
    public async Task MetaDL_sets_the_fetching_metadata_signal_even_before_the_stall_window()
    {
        var addedUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        var item = await RecordedSnapshot("metaDL", size: 0, progress: 0, addedUtc, capturedUtc: addedUtc.AddMinutes(1));

        Assert.True(item.IsFetchingMetadata);
    }

    [Theory]
    [InlineData("downloading")]
    [InlineData("stalledDL")]
    public async Task Every_other_state_leaves_the_fetching_metadata_signal_false(string state)
    {
        var addedUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        var item = await RecordedSnapshot(state, size: 0, progress: 0, addedUtc, capturedUtc: addedUtc.AddMinutes(1));

        Assert.False(item.IsFetchingMetadata);
    }

    private static async Task<DownloadQueueItem> RecordedSnapshot(
        string state,
        long size,
        double progress,
        DateTimeOffset addedUtc,
        DateTimeOffset capturedUtc)
    {
        // qBittorrent 5.2.1's recorded list of a magnet whose metadata never came, in the state, size and age under test.
        var torrentsInfo = RecordedQbittorrentRig.Info(
            RecordedQbittorrentRig.NoMetadata, ("category", "tv"), ("state", state), ("progress", progress), ("size", size), ("added_on", addedUtc.ToUnixTimeSeconds()));
        var handler = RecordedQbittorrentRig.Handler(torrentsInfo);
        var client = new QbittorrentDownloadClient(() => handler);
        var snapshot = await client.GetSnapshotAsync(Client(), capturedUtc, CancellationToken.None);
        return Assert.Single(snapshot!.Queue);
    }

    private static DownloadClientItem Client()
        => new("qbit-1", "qBittorrent", "qbittorrent", "localhost", 8080, null, null, "http://localhost:8080/", false, null, "movies", "tv", null, 1, true,
            "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
