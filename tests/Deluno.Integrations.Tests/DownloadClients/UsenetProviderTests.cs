using Deluno.Connections.Usenet;
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

    // ------------------------------------------------------------ NZBGet: saving the provider

    /// <summary>NZBGet as it really behaves: saveconfig writes the file, and `config` keeps answering the old settings until a moment after reload.</summary>
    private sealed class NzbGetHandler(int staleReadsAfterReload) : HttpMessageHandler
    {
        private Dictionary<string, string> _live = StockNzbGet.ToDictionary(o => o.Key, o => o.Value);
        private Dictionary<string, string>? _saved;
        private int _stale = -1;

        public int ConfigReads { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = System.Text.Json.JsonDocument.Parse(body);
            var method = document.RootElement.GetProperty("method").GetString();
            string answer;
            switch (method)
            {
                case "config":
                    ConfigReads++;
                    if (_stale > 0) { _stale--; }
                    else if (_stale == 0 && _saved is not null) { _live = _saved; _saved = null; _stale = -1; }
                    answer = System.Text.Json.JsonSerializer.Serialize(new { result = _live.Select(pair => new { Name = pair.Key, Value = pair.Value }).ToArray() });
                    break;
                case "saveconfig":
                    _saved = document.RootElement.GetProperty("params")[0].EnumerateArray()
                        .ToDictionary(item => item.GetProperty("Name").GetString()!, item => item.GetProperty("Value").GetString()!);
                    answer = """{"result":true}""";
                    break;
                case "reload":
                    _stale = staleReadsAfterReload;
                    answer = """{"result":true}""";
                    break;
                default:
                    answer = """{"result":true}""";
                    break;
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }

    private static DownloadClientItem NzbGetClient()
        => new("nzb-1", "NZBGet", "nzbget", "localhost", 6789, "nzbget", "tegbzn6789", "http://localhost:6789/", false, null,
            "Movies", "TV", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task The_provider_saved_to_NZBGet_is_kept_even_when_NZBGet_applies_the_reload_a_moment_late(int staleReads)
    {
        var handler = new NzbGetHandler(staleReads);

        var result = await new NzbGetDownloadClient(new Factory(handler), TimeSpan.Zero).SetUsenetProviderAsync(NzbGetClient(), Provider, CancellationToken.None);

        Assert.True(result!.Saved, result.Message);
        Assert.Contains("news.example.com", result.Message);
    }

    [Fact]
    public async Task A_provider_NZBGet_never_applies_is_still_reported_as_not_kept()
    {
        var handler = new NzbGetHandler(staleReadsAfterReload: int.MaxValue);

        var result = await new NzbGetDownloadClient(new Factory(handler), TimeSpan.Zero).SetUsenetProviderAsync(NzbGetClient(), Provider, CancellationToken.None);

        Assert.False(result!.Saved);
        Assert.Contains("did not keep", result.Message);
    }

    [Fact]
    public void Only_the_provider_just_written_counts_not_an_older_working_one()
    {
        var older = new[] { O("Server1.Active", "yes"), O("Server1.Host", "old.example.com") };

        Assert.True(NzbGetServerPlan.HasWorkingServer(older));
        Assert.False(NzbGetServerPlan.HasWorkingServer(older, "news.example.com"));
        Assert.True(NzbGetServerPlan.HasWorkingServer(older, "OLD.example.com"));
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
