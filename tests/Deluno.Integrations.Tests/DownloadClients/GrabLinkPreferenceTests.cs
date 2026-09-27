using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search.Trackers;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// A prefer-magnet-over-torrent-file toggle for torrent indexers (#752):
/// when a grab has both a magnet URL and a .torrent file, the indexer's own
/// setting decides which one Deluno sends to the download client.
/// </summary>
public sealed class GrabLinkPreferenceTests
{
    [Fact]
    public void Prefers_the_torrent_file_by_default_when_both_are_available()
    {
        var grab = new TrackerGrab("magnet:?xt=urn:btih:abc", [1, 2, 3], "https://tracker.example.test/dl/1");

        var chosen = GrabLinkPreference.Choose(grab, preferMagnetUrl: false);

        Assert.Null(chosen.MagnetUrl);
        Assert.Equal(grab.TorrentFile, chosen.TorrentFile);
    }

    [Fact]
    public void Prefers_the_magnet_link_when_the_setting_is_on_and_both_are_available()
    {
        var grab = new TrackerGrab("magnet:?xt=urn:btih:abc", [1, 2, 3], "https://tracker.example.test/dl/1");

        var chosen = GrabLinkPreference.Choose(grab, preferMagnetUrl: true);

        Assert.Equal("magnet:?xt=urn:btih:abc", chosen.MagnetUrl);
        Assert.Null(chosen.TorrentFile);
    }

    [Fact]
    public void Uses_the_torrent_file_when_no_magnet_is_available_regardless_of_the_setting()
    {
        var grab = new TrackerGrab(null, [1, 2, 3], "https://tracker.example.test/dl/1");

        Assert.Equal(grab.TorrentFile, GrabLinkPreference.Choose(grab, preferMagnetUrl: true).TorrentFile);
        Assert.Equal(grab.TorrentFile, GrabLinkPreference.Choose(grab, preferMagnetUrl: false).TorrentFile);
    }

    [Fact]
    public void Uses_the_magnet_link_when_no_torrent_file_is_available_regardless_of_the_setting()
    {
        var grab = new TrackerGrab("magnet:?xt=urn:btih:abc", null, "https://tracker.example.test/dl/1");

        Assert.Equal("magnet:?xt=urn:btih:abc", GrabLinkPreference.Choose(grab, preferMagnetUrl: true).MagnetUrl);
        Assert.Equal("magnet:?xt=urn:btih:abc", GrabLinkPreference.Choose(grab, preferMagnetUrl: false).MagnetUrl);
    }
}
