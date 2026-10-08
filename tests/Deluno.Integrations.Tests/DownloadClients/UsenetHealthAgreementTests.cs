using System.Net;
using System.Text;
using Deluno.Connections;
using Deluno.Connections.Contracts;
using Deluno.Connections.Usenet;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1142: the Download clients list (the stored result of a test), the drawer's Test and telemetry must give one verdict
/// for a Usenet client with no news server, because they all read the client the same way.
/// </summary>
public sealed class UsenetHealthAgreementTests
{
    private const string SabNoServerAtAll = """{"config":{}}""";
    private const string SabWorking = """{"config":{"servers":[{"name":"x","host":"news.example.com","enable":"1"}]}}""";
    private const string NzbGetPlaceholder = """{"result":[{"Name":"Server1.Active","Value":"yes"},{"Name":"Server1.Host","Value":"my.newsserver.com"}]}""";
    private const string NzbGetWorking = """{"result":[{"Name":"Server1.Active","Value":"yes"},{"Name":"Server1.Host","Value":"news.example.com"}]}""";

    [Fact]
    public void SABnzbd_answering_an_empty_config_has_no_provider()
    {
        using var document = System.Text.Json.JsonDocument.Parse(SabNoServerAtAll);
        Assert.False(UsenetProviderCheck.SabnzbdHasProvider(document.RootElement));
    }

    [Fact]
    public void An_answer_that_is_not_a_config_says_nothing()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""{"error":"API Key Incorrect"}""");
        Assert.Null(UsenetProviderCheck.SabnzbdHasProvider(document.RootElement));
        Assert.Null(UsenetProviderCheck.NzbGetHasProvider(document.RootElement));
    }

    [Theory]
    [InlineData("sabnzbd", SabNoServerAtAll, "degraded")]
    [InlineData("sabnzbd", SabWorking, "healthy")]
    [InlineData("nzbget", NzbGetPlaceholder, "degraded")]
    [InlineData("nzbget", NzbGetWorking, "healthy")]
    public async Task The_test_and_telemetry_give_the_same_verdict(string protocol, string configAnswer, string expected)
    {
        using var server = new FakeUsenetClient(protocol, configAnswer);
        var item = server.Item();

        var test = await ConnectionsEndpointRouteBuilderExtensions.TestDownloadClientAsync(item, CancellationToken.None);
        var snapshot = protocol == "sabnzbd"
            ? await new SabnzbdDownloadClient(new PlainFactory()).GetSnapshotAsync(item, DateTimeOffset.UtcNow, CancellationToken.None)
            : await new NzbGetDownloadClient(new PlainFactory()).GetSnapshotAsync(item, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(expected, test.healthStatus);
        Assert.Equal(expected, snapshot!.HealthStatus);
        if (expected == "degraded")
        {
            Assert.Equal(UsenetProviderText.NoProvider, test.message);
            Assert.Equal(UsenetProviderText.NoProvider, snapshot.LastHealthMessage);
        }
    }

    [Theory]
    [InlineData("sabnzbd", SabNoServerAtAll)]
    [InlineData("nzbget", NzbGetPlaceholder)]
    public async Task A_paused_client_with_no_provider_says_so_and_is_not_shown_as_paused(string protocol, string configAnswer)
    {
        using var server = new FakeUsenetClient(protocol, configAnswer, paused: true);
        var item = server.Item();

        var snapshot = protocol == "sabnzbd"
            ? await new SabnzbdDownloadClient(new PlainFactory()).GetSnapshotAsync(item, DateTimeOffset.UtcNow, CancellationToken.None)
            : await new NzbGetDownloadClient(new PlainFactory()).GetSnapshotAsync(item, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal("degraded", snapshot!.HealthStatus);
        Assert.Equal(UsenetProviderText.NoProvider, snapshot.LastHealthMessage);
        Assert.NotEqual(true, snapshot.ClientPaused);
    }

    [Theory]
    [InlineData("sabnzbd", SabWorking)]
    [InlineData("nzbget", NzbGetWorking)]
    public async Task A_paused_client_with_a_provider_is_still_shown_as_paused(string protocol, string configAnswer)
    {
        using var server = new FakeUsenetClient(protocol, configAnswer, paused: true);
        var item = server.Item();

        var snapshot = protocol == "sabnzbd"
            ? await new SabnzbdDownloadClient(new PlainFactory()).GetSnapshotAsync(item, DateTimeOffset.UtcNow, CancellationToken.None)
            : await new NzbGetDownloadClient(new PlainFactory()).GetSnapshotAsync(item, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(true, snapshot!.ClientPaused);
    }

    private sealed class PlainFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    /// <summary>A tiny local server that answers like SABnzbd or NZBGet.</summary>
    private sealed class FakeUsenetClient : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly string _protocol;
        private readonly int _port;

        public FakeUsenetClient(string protocol, string configAnswer, bool paused = false)
        {
            _protocol = protocol;
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            _port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await _listener.GetContextAsync(); }
                    catch { return; }

                    var query = context.Request.Url!.PathAndQuery;
                    string body;
                    if (protocol == "sabnzbd")
                    {
                        body = query.Contains("mode=get_config", StringComparison.Ordinal) ? configAnswer
                            : query.Contains("mode=queue", StringComparison.Ordinal) ? $$$"""{"queue":{"slots":[],"speed":"0","paused":{{{(paused ? "true" : "false")}}},"diskspace1":"100.0","diskspacetotal1":"500.0","have_warnings":"0"}}"""
                            : query.Contains("mode=history", StringComparison.Ordinal) ? """{"history":{"slots":[]}}"""
                            : """{"version":"5.0.4"}""";
                    }
                    else
                    {
                        using var reader = new StreamReader(context.Request.InputStream);
                        var request = await reader.ReadToEndAsync();
                        body = request.Contains("\"config\"", StringComparison.Ordinal) ? configAnswer
                            : request.Contains("\"listgroups\"", StringComparison.Ordinal) || request.Contains("\"history\"", StringComparison.Ordinal) ? """{"result":[]}"""
                            : request.Contains("\"status\"", StringComparison.Ordinal) ? $$$"""{"result":{"FreeDiskSpaceMB":100000,"DownloadRate":0,"DownloadPaused":{{{(paused ? "true" : "false")}}}}}"""
                            : """{"result":"23.0"}""";
                    }

                    var bytes = Encoding.UTF8.GetBytes(body);
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            });
        }

        public DownloadClientItem Item()
            => new("c-1", _protocol, _protocol, "127.0.0.1", _port, null, "apikey", $"http://127.0.0.1:{_port}/", false, null,
                "Movies", "TV", null, 1, true, "unknown", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        public void Dispose() => _listener.Close();
    }
}
