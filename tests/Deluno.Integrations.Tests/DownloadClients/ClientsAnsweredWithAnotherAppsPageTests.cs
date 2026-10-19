using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using static Deluno.Integrations.Tests.DownloadClients.RecordedClientCalls;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>#1362: no client's action throws when its address answers with another app's web page (not JSON, not XML): it is a plain failure.</summary>
public sealed class ClientsAnsweredWithAnotherAppsPageTests
{
    private sealed class Page(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("<html><body>Sign in to another app</body></html>", Encoding.UTF8, "text/html") });
    }

    private static IDownloadClient Build(string protocol, HttpMessageHandler handler) => protocol switch
    {
        "sabnzbd" => new SabnzbdDownloadClient(new Factory(handler)),
        "nzbget" => new NzbGetDownloadClient(new Factory(handler)),
        "transmission" => new TransmissionDownloadClient(new Factory(handler)),
        "deluge" => new DelugeDownloadClient(new Factory(handler)),
        "utorrent" => new UTorrentDownloadClient(() => handler),
        "qbittorrent" => new QbittorrentDownloadClient(() => handler),
        "rtorrent" => new RtorrentDownloadClient(new Factory(handler)),
        _ => throw new ArgumentOutOfRangeException(nameof(protocol))
    };

    [Theory]
    [InlineData("sabnzbd", DownloadClientActions.Pause)]
    [InlineData("sabnzbd", DownloadClientActions.Resume)]
    [InlineData("sabnzbd", DownloadClientActions.MoveToTop)]
    [InlineData("sabnzbd", DownloadClientActions.DeleteWithData)]
    [InlineData("nzbget", DownloadClientActions.Pause)]
    [InlineData("nzbget", DownloadClientActions.Resume)]
    [InlineData("nzbget", DownloadClientActions.MoveToTop)]
    [InlineData("nzbget", DownloadClientActions.DeleteWithData)]
    [InlineData("transmission", DownloadClientActions.Pause)]
    [InlineData("transmission", DownloadClientActions.Resume)]
    [InlineData("transmission", DownloadClientActions.DeleteWithData)]
    [InlineData("deluge", DownloadClientActions.Pause)]
    [InlineData("deluge", DownloadClientActions.Resume)]
    [InlineData("deluge", DownloadClientActions.DeleteWithData)]
    [InlineData("utorrent", DownloadClientActions.Pause)]
    [InlineData("utorrent", DownloadClientActions.Resume)]
    [InlineData("utorrent", DownloadClientActions.DeleteWithData)]
    [InlineData("qbittorrent", DownloadClientActions.Pause)]
    [InlineData("qbittorrent", DownloadClientActions.Resume)]
    [InlineData("qbittorrent", DownloadClientActions.DeleteWithData)]
    [InlineData("rtorrent", DownloadClientActions.Pause)]
    [InlineData("rtorrent", DownloadClientActions.Resume)]
    [InlineData("rtorrent", DownloadClientActions.DeleteWithData)]
    public async Task An_action_against_another_apps_page_is_a_plain_failure_not_an_exception(string protocol, string action)
    {
        var client = Build(protocol, new Page(HttpStatusCode.OK));

        var result = await Record.ExceptionAsync(async () =>
        {
            var done = await client.ExecuteActionAsync(Client(protocol, "user", "secret"), action, "aed3fbe9593f02c2fe022a2fbd16704709e2fd84", CancellationToken.None);
            Assert.False(done.Succeeded, done.Message);
        });

        Assert.Null(result);
    }
}
