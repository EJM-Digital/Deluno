using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;
using static Deluno.Integrations.Tests.DownloadClients.RecordedClientCalls;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// The Re-check button on a download's drawer is the same on every client. A torrent client has a recheck of its own; SABnzbd and
/// NZBGet verify their own downloads and have no per-item recheck, so the press is refused in plain words and the client is not
/// sent anything at all, never mapped onto a neighbouring verb (a pause, a delete, a re-queue).
/// </summary>
public sealed class AUsenetClientHasNothingToRecheckTests
{
    [Fact]
    public async Task SABnzbd_refuses_a_recheck_in_plain_words_and_is_sent_nothing()
    {
        var calls = new Calls("sabnzbd", [RecordedReplies.Sabnzbd("idle"), RecordedReplies.Sabnzbd("version")]);
        var client = new SabnzbdDownloadClient(new Factory(calls.Build()));

        var result = await client.ExecuteActionAsync(Client("sabnzbd", "SABnzbd", "key"), DownloadClientActions.Recheck, "nzo-1", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("SABnzbd does not support this action.", result.Message);
        Assert.False(client.Capabilities.SupportsRecheck);
        Assert.Empty(calls.Log);
    }

    [Fact]
    public async Task NZBGet_refuses_a_recheck_in_plain_words_and_is_sent_nothing()
    {
        var calls = new Calls("nzbget", [RecordedReplies.Nzbget("idle"), RecordedReplies.Nzbget("version")]);
        var client = new NzbGetDownloadClient(new Factory(calls.Build()));

        var result = await client.ExecuteActionAsync(Client("nzbget", "nzbget", "secret"), DownloadClientActions.Recheck, "42", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("NZBGet does not support this action.", result.Message);
        Assert.False(client.Capabilities.SupportsRecheck);
        Assert.Empty(calls.Log);
    }

    [Fact]
    public void Every_torrent_client_that_has_a_recheck_says_so_and_the_usenet_clients_say_they_have_none()
    {
        var torrentClients = new IDownloadClient[]
        {
            new QbittorrentDownloadClient(() => new HttpClientHandler()),
            new TransmissionDownloadClient(new Factory(new HttpClientHandler())),
            new DelugeDownloadClient(new Factory(new HttpClientHandler()))
        };
        Assert.All(torrentClients, client => Assert.True(client.Capabilities.SupportsRecheck, client.Protocol));
        Assert.False(new SabnzbdDownloadClient(new Factory(new HttpClientHandler())).Capabilities.SupportsRecheck);
        Assert.False(new NzbGetDownloadClient(new Factory(new HttpClientHandler())).Capabilities.SupportsRecheck);
    }
}
