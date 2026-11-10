using Deluno.Tests.Recorded;
using Xunit;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1535: many tests read two torrents from two recordings and mean "two different downloads" (a queue with a downloading
/// one and a finished one, a stalled one beside a removed one). If a recording is made again with the same torrent in both,
/// those tests would quietly stop proving what they say. This fails first and names the pair.
/// </summary>
public sealed class RecordedTorrentsThatTestsTreatAsDifferentAreDifferentTests
{
    [Fact]
    public void The_qbittorrent_recordings_tests_pair_up_hold_different_torrents()
        => AssertDistinct("qbittorrent", "downloading", "category-moved", "paused", "finished-folder", "resumed", "failed");

    [Fact]
    public void The_transmission_recordings_tests_pair_up_hold_different_torrents()
        => AssertDistinct("transmission", "duplicate-add", "finished-folder", "paused", "resumed", "failed");

    [Fact]
    public void The_deluge_recordings_tests_pair_up_hold_different_torrents()
        => AssertDistinct("deluge", "paused", "duplicate-add", "finished-folder", "resumed", "failed", "downloading");

    [Fact]
    public void The_release_the_indexer_recording_offers_is_not_a_torrent_a_client_recording_holds()
        => Assert.NotEqual(RecordedReplies.Jackett("search-movie-imdb").Hash(), RecordedReplies.Qbittorrent("category-moved").Hash(), StringComparer.OrdinalIgnoreCase);

    private static void AssertDistinct(string app, params string[] states)
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in states)
        {
            var hash = RecordedReplies.LoadLatest(app, state).Hash();
            Assert.False(seen.TryGetValue(hash, out var other), $"{app} '{state}' and '{other}' now hold the same torrent ({hash}); tests that treat them as two downloads would prove nothing. Record them with different torrents.");
            seen[hash] = state;
        }
    }
}
