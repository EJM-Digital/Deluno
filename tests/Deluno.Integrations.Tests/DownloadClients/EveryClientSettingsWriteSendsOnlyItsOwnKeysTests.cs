using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Deluno.Connections.Contracts;
using Deluno.Connections.Usenet;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1257, "look next door": NZBGet's save sent back a whole settings list it had read, runtime-only values included. The writes
/// Deluno itself makes to the other clients (the installer's are pinned in the tray tests) send only the keys they mean to
/// change, and every key is a real setting of that app, checked against the app's recorded reply.
/// </summary>
public sealed class EveryClientSettingsWriteSendsOnlyItsOwnKeysTests
{
    private static DownloadClientItem Client(string protocol, int port)
        => new("c-1", protocol, protocol, "localhost", port, "user", "secret-key", $"http://localhost:{port}/", false, null,
            "movies", "tv", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class Capture(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(string Url, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.ToString(), request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return answer(request);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    [Fact]
    public async Task SABnzbd_set_config_for_the_provider_sends_only_the_server_keys_and_all_are_real_server_settings()
    {
        var recordedServer = RecordedReplies.Sabnzbd("settings").Find("mode=get_config").Reply.Json()["config"]!["servers"]![0]!.AsObject()
            .Select(p => p.Key).ToHashSet();
        var handler = new Capture(request => request.RequestUri!.Query.Contains("mode=get_config", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"config":{"servers":[{"name":"x","host":"news.example.com","enable":1}]}}""", Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"status":true}""", Encoding.UTF8, "application/json") });

        await new SabnzbdDownloadClient(new Factory(handler))
            .SetUsenetProviderAsync(Client("sabnzbd", 8085), new UsenetProviderRequest("news.example.com", 563, true, "jo", "pw", 8), CancellationToken.None);

        var write = System.Web.HttpUtility.ParseQueryString(new Uri(Assert.Single(handler.Requests, r => r.Url.Contains("mode=set_config", StringComparison.Ordinal)).Url).Query);
        var keys = write.AllKeys.Select(k => k!).ToHashSet();
        Assert.Equal(new HashSet<string> { "mode", "section", "keyword", "displayname", "host", "port", "username", "password", "connections", "ssl", "enable", "output", "apikey" }, keys);
        Assert.Subset(recordedServer, keys.Except(["mode", "section", "keyword", "output", "apikey"]).ToHashSet());
    }

    [Fact]
    public async Task qBittorrent_setPreferences_for_the_content_blocklist_sends_only_excluded_file_names_which_is_a_real_preference()
    {
        var handler = new Capture(request => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Ok.", Encoding.UTF8, "text/plain") });

        var (succeeded, message) = await new QbittorrentDownloadClient(() => handler)
            .PushExcludedFileNamesAsync(Client("qbittorrent", 8080), ["*.exe"], CancellationToken.None);

        Assert.True(succeeded, message);
        var form = System.Web.HttpUtility.ParseQueryString(Assert.Single(handler.Requests, r => r.Url.Contains("app/setPreferences", StringComparison.Ordinal)).Body);
        Assert.Equal("json", Assert.Single(form.AllKeys));
        var sent = JsonNode.Parse(form["json"]!)!.AsObject().Select(p => p.Key).ToHashSet();
        Assert.Equal(new HashSet<string> { "excluded_file_names" }, sent);
        var recorded = RecordedReplies.Qbittorrent("settings").Find("app/preferences").Reply.Json().AsObject().Select(p => p.Key).ToHashSet();
        Assert.Subset(recorded, sent);
    }
}
