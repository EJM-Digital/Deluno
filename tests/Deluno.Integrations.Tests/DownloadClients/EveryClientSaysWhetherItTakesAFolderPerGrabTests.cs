using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1073: Transmission and Deluge take the library's downloads folder on every
/// grab. The answer must be the one callers get through <see cref="IDownloadClient"/>
/// from the real client classes: a property declared only on the class, with
/// the interface reached through a base class, is never asked.
/// </summary>
public sealed class EveryClientSaysWhetherItTakesAFolderPerGrabTests
{
    private static readonly IHttpClientFactory Http = new NoHttp();

    [Fact]
    public void Transmission_and_Deluge_take_a_folder_per_grab_through_the_interface()
    {
        IDownloadClient transmission = new TransmissionDownloadClient(Http);
        IDownloadClient deluge = new DelugeDownloadClient(Http);

        Assert.True(transmission.SetsSaveFolderPerGrab);
        Assert.True(deluge.SetsSaveFolderPerGrab);
    }

    [Fact]
    public void Clients_with_their_own_categories_do_not()
    {
        IDownloadClient qbittorrent = new QbittorrentDownloadClient(() => new HttpClientHandler());

        Assert.False(qbittorrent.SetsSaveFolderPerGrab);
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
