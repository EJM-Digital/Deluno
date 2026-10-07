using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1142: SABnzbd and NZBGet cannot download without a news server. Deluno says so instead of "Healthy",
/// takes the provider once, and writes it to both clients.
/// </summary>
public sealed class UsenetProviderTests
{
    private static readonly UsenetProviderRequest Provider = new("news.example.com", 563, true, "jo", "pw", 8);

    private static KeyValuePair<string, string> O(string name, string value) => new(name, value);

    private static readonly KeyValuePair<string, string>[] StockNzbGet =
    [
        O("MainDir", "C:\\ProgramData\\NZBGet"),
        O("Server1.Active", "yes"),
        O("Server1.Name", "my news server"),
        O("Server1.Host", "my.newsserver.com"),
        O("Server1.Port", "119"),
        O("Server1.Connections", "4")
    ];

    // ------------------------------------------------------------ the plan for NZBGet

    [Fact]
    public void The_stock_placeholder_server_of_NZBGet_is_not_a_provider()
    {
        Assert.False(NzbGetServerPlan.HasWorkingServer(StockNzbGet));
    }

    [Fact]
    public void The_provider_replaces_the_placeholder_in_its_slot_and_keeps_every_other_option()
    {
        var plan = NzbGetServerPlan.Apply(StockNzbGet, Provider).ToDictionary(o => o.Key, o => o.Value);

        Assert.Equal("news.example.com", plan["Server1.Host"]);
        Assert.Equal("563", plan["Server1.Port"]);
        Assert.Equal("yes", plan["Server1.Encryption"]);
        Assert.Equal("8", plan["Server1.Connections"]);
        Assert.Equal("jo", plan["Server1.Username"]);
        Assert.Equal("C:\\ProgramData\\NZBGet", plan["MainDir"]);
        Assert.True(NzbGetServerPlan.HasWorkingServer(plan.ToArray()));
    }

    [Fact]
    public void Saving_again_updates_the_same_server_instead_of_adding_another()
    {
        var once = NzbGetServerPlan.Apply(StockNzbGet, Provider);
        var twice = NzbGetServerPlan.Apply(once, Provider with { Host = "other.example.com" });

        Assert.Equal(once.Count, twice.Count);
        Assert.Equal("other.example.com", twice.Single(o => o.Key == "Server1.Host").Value);
    }

    [Fact]
    public void A_real_server_added_by_hand_is_left_alone_and_the_provider_gets_the_next_slot()
    {
        var byHand = new[] { O("Server1.Active", "yes"), O("Server1.Name", "mine"), O("Server1.Host", "mine.example.com") };

        var plan = NzbGetServerPlan.Apply(byHand, Provider).ToDictionary(o => o.Key, o => o.Value);

        Assert.Equal("mine.example.com", plan["Server1.Host"]);
        Assert.Equal("news.example.com", plan["Server2.Host"]);
    }

    [Theory]
    [InlineData("", 563, 8, "address")]
    [InlineData("my.newsserver.com", 563, 8, "address")]
    [InlineData("https://news.example.com", 563, 8, "without")]
    [InlineData("news.example.com", 0, 8, "port")]
    [InlineData("news.example.com", 563, 0, "Connections")]
    public void A_provider_that_cannot_work_is_refused_in_plain_words(string host, int port, int connections, string mention)
    {
        var problem = UsenetProviderText.Validate(new(host, port, true, null, null, connections));

        Assert.NotNull(problem);
        Assert.Contains(mention, problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_complete_provider_is_accepted()
    {
        Assert.Null(UsenetProviderText.Validate(Provider));
    }

    // ------------------------------------------------------------ SABnzbd

    [Fact]
    public async Task SABnzbd_with_no_news_server_is_not_healthy()
    {
        var handler = new SabHandler(servers: "[]");
        var snapshot = await new SabnzbdDownloadClient(new Factory(handler)).GetSnapshotAsync(SabClient(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal("degraded", snapshot!.HealthStatus);
        Assert.Equal(UsenetProviderText.NoProvider, snapshot.LastHealthMessage);
    }

    [Fact]
    public async Task SABnzbd_with_a_working_news_server_stays_healthy()
    {
        var handler = new SabHandler(servers: """[{"name":"x","host":"news.example.com","enable":"1"}]""");
        var snapshot = await new SabnzbdDownloadClient(new Factory(handler)).GetSnapshotAsync(SabClient(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal("healthy", snapshot!.HealthStatus);
    }

    [Fact]
    public async Task The_provider_is_written_to_SABnzbd_and_read_back()
    {
        var handler = new SabHandler(servers: "[]") { KeepSetServer = true };
        var result = await new SabnzbdDownloadClient(new Factory(handler)).SetUsenetProviderAsync(SabClient(), Provider, CancellationToken.None);

        Assert.True(result!.Saved);
        var set = Assert.Single(handler.Requests, uri => uri.Contains("mode=set_config", StringComparison.Ordinal));
        Assert.Contains("section=servers", set);
        Assert.Contains("host=news.example.com", set);
        Assert.Contains("port=563", set);
        Assert.Contains("ssl=1", set);
        Assert.Contains("connections=8", set);
    }

    [Fact]
    public async Task A_client_that_takes_no_provider_is_not_asked()
    {
        var result = await new TransmissionDownloadClient(new Factory(new SabHandler("[]"))).SetUsenetProviderAsync(SabClient(), Provider, CancellationToken.None);

        Assert.Null(result);
    }

    private static DownloadClientItem SabClient()
        => new("sab-1", "SABnzbd", "sabnzbd", "localhost", 8085, null, "apikey", "http://localhost:8085/", false, null,
            "Movies", "TV", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class SabHandler(string servers) : HttpMessageHandler
    {
        private string _servers = servers;

        public List<string> Requests { get; } = [];

        public bool KeepSetServer { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.PathAndQuery;
            Requests.Add(uri);
            string body;
            if (uri.Contains("mode=set_config", StringComparison.Ordinal))
            {
                if (KeepSetServer)
                {
                    _servers = """[{"name":"Deluno Usenet provider","host":"news.example.com","enable":1}]""";
                }

                body = """{"config":{}}""";
            }
            else if (uri.Contains("mode=get_config", StringComparison.Ordinal))
            {
                body = "{\"config\":{\"servers\":" + _servers + "}}";
            }
            else if (uri.Contains("mode=queue", StringComparison.Ordinal))
            {
                body = """{"queue":{"slots":[],"speed":"0","paused":false,"diskspace1":"100.0","diskspacetotal1":"500.0","have_warnings":"0"}}""";
            }
            else if (uri.Contains("mode=history", StringComparison.Ordinal))
            {
                body = """{"history":{"slots":[]}}""";
            }
            else
            {
                body = """{"version":"5.0.4"}""";
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
