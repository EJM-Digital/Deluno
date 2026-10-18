using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using static Deluno.Integrations.Tests.DownloadClients.RecordedClientCalls;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// The Transfers drawer shows reorder and force-start buttons only when the client says it has them, so each client's own word is
/// pinned here, one row per client: a client whose queue API could not be confirmed (uTorrent, rTorrent) offers no reorder, and
/// only qBittorrent has force-start.
/// </summary>
public sealed class EveryClientSaysWhichControlsItHasTests
{
    private static IDownloadClient ClientFor(string protocol)
    {
        var handler = new HttpClientHandler();
        return protocol switch
        {
            "qbittorrent" => new QbittorrentDownloadClient(() => handler),
            "transmission" => new TransmissionDownloadClient(new Factory(handler)),
            "deluge" => new DelugeDownloadClient(new Factory(handler)),
            "utorrent" => new UTorrentDownloadClient(() => handler),
            "rtorrent" => new RtorrentDownloadClient(new Factory(handler)),
            "sabnzbd" => new SabnzbdDownloadClient(new Factory(handler)),
            "nzbget" => new NzbGetDownloadClient(new Factory(handler)),
            _ => throw new ArgumentOutOfRangeException(nameof(protocol))
        };
    }

    [Theory]
    //   protocol       reorder force-start re-check pause/resume
    [InlineData("qbittorrent", true, true, true, true)]
    [InlineData("transmission", true, false, true, true)]
    [InlineData("deluge", true, false, true, true)]
    [InlineData("sabnzbd", true, false, false, true)]
    [InlineData("nzbget", true, false, false, true)]
    [InlineData("utorrent", false, false, true, true)]
    [InlineData("rtorrent", false, false, true, true)]
    public void Each_client_says_the_controls_it_has(string protocol, bool reorder, bool forceStart, bool recheck, bool pauseResume)
    {
        var capabilities = ClientFor(protocol).Capabilities;

        Assert.Equal(reorder, capabilities.SupportsQueueReorder);
        Assert.Equal(forceStart, capabilities.SupportsForceStart);
        Assert.Equal(recheck, capabilities.SupportsRecheck);
        Assert.Equal(pauseResume, capabilities.SupportsPauseResume);
    }
}
