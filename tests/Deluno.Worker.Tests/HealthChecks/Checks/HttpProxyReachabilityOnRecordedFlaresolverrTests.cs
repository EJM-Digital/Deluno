using System.Net;
using Deluno.Connections.Contracts;
using Deluno.Tests.Recorded;
using Deluno.Worker.HealthChecks;
using Deluno.Worker.HealthChecks.Checks;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>
/// #1301: the reachability test of a FlareSolverr proxy (it asks <c>sessions.list</c>), run against what FlareSolverr 3.5.2
/// really answered (tests/fixtures/recorded/flaresolverr): a good answer, an HTTP error, a refused connection. A forward proxy
/// with no port in its address is tried on the usual one for its kind.
/// </summary>
public sealed class HttpProxyReachabilityOnRecordedFlaresolverrTests
{
    [Fact]
    public async Task A_flaresolverr_proxy_that_answers_sessions_list_with_status_ok_is_reachable_and_was_asked_on_its_v1_address()
    {
        var asked = new List<(HttpMethod Method, string Path, string Body)>();
        var handler = RecordedReplies.Handler("flaresolverr", [RecordedReplies.Flaresolverr("sessions-empty")], (request, body) =>
        {
            asked.Add((request.Method, request.RequestUri!.AbsolutePath, body));
            return null;
        });

        var result = await CreateTester(handler).TestAsync(Proxy(ProxyTypes.FlareSolverr, "http://127.0.0.1:8191"), CancellationToken.None);

        Assert.True(result.Reachable);
        Assert.Contains("answered its sessions.list command", result.Message);
        var call = Assert.Single(asked);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("/v1", call.Path);
        Assert.Contains("sessions.list", call.Body);
    }

    [Fact]
    public async Task An_address_that_already_ends_in_v1_is_not_given_a_second_v1()
    {
        var asked = new List<string>();
        var handler = RecordedReplies.Handler("flaresolverr", [RecordedReplies.Flaresolverr("sessions-empty")], (request, _) =>
        {
            asked.Add(request.RequestUri!.AbsolutePath);
            return null;
        });

        var result = await CreateTester(handler).TestAsync(Proxy(ProxyTypes.FlareSolverr, "http://127.0.0.1:8191/v1/"), CancellationToken.None);

        Assert.True(result.Reachable);
        Assert.Equal(["/v1"], asked);
    }

    [Fact]
    public async Task A_flaresolverr_proxy_that_answers_with_an_http_error_is_not_reachable_and_the_status_is_named()
    {
        var refused = RecordedReplies.Flaresolverr("unknown-command").Single.Reply;

        var result = await CreateTester(new FixedReply((HttpStatusCode)refused.Status, refused.Body ?? string.Empty))
            .TestAsync(Proxy(ProxyTypes.FlareSolverr, "http://127.0.0.1:8191"), CancellationToken.None);

        Assert.False(result.Reachable);
        Assert.Contains("answered with HTTP 500", result.Message);
    }

    [Fact]
    public async Task A_flaresolverr_proxy_that_answers_200_with_a_status_other_than_ok_is_not_reachable()
    {
        // The recorded sessions.list reply with only its status word changed: FlareSolverr's own 200 body carrying "error".
        var ok = RecordedReplies.Flaresolverr("sessions-empty").Single.Reply;

        var result = await CreateTester(new FixedReply(HttpStatusCode.OK, ok.BodyWith(("status", "error"))))
            .TestAsync(Proxy(ProxyTypes.FlareSolverr, "http://127.0.0.1:8191"), CancellationToken.None);

        Assert.False(result.Reachable);
        Assert.Contains("did not answer sessions.list with status ok", result.Message);
    }

    [Fact]
    public async Task A_flaresolverr_proxy_whose_port_refuses_the_connection_is_not_reachable_and_says_where_it_looked()
    {
        var result = await CreateTester(RecordedReplies.Flaresolverr("not-answering").NotAnswering())
            .TestAsync(Proxy(ProxyTypes.FlareSolverr, "http://127.0.0.1:8191/"), CancellationToken.None);

        Assert.False(result.Reachable);
        Assert.Contains("could not reach Test proxy at http://127.0.0.1:8191", result.Message);
    }

    [Theory]
    [InlineData(ProxyTypes.Http, "http://127.0.0.1", "127.0.0.1:8080")]
    [InlineData(ProxyTypes.Http, "127.0.0.1", "127.0.0.1:8080")]
    [InlineData(ProxyTypes.Socks5, "socks5://127.0.0.1", "127.0.0.1:1080")]
    [InlineData(ProxyTypes.Socks4, "127.0.0.1", "127.0.0.1:1080")]
    public async Task A_forward_proxy_address_with_no_port_is_tried_on_8080_for_http_and_1080_for_socks(string kind, string address, string looked)
    {
        var result = await CreateTester(new FixedReply(HttpStatusCode.OK, "{}")).TestAsync(Proxy(kind, address), CancellationToken.None);

        // Whether something happens to listen there or not, the message names the host and port that was tried.
        Assert.Contains(looked, result.Message);
    }

    private sealed class FixedReply(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
    }

    private static HttpProxyReachabilityTester CreateTester(HttpMessageHandler handler)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(HealthCheckHttpClients.Proxy, client => client.Timeout = TimeSpan.FromSeconds(3))
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        return new HttpProxyReachabilityTester(services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>());
    }

    private static ProxyItem Proxy(string proxyType, string address)
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        return new ProxyItem(
            Id: Guid.NewGuid().ToString("N"),
            Name: "Test proxy",
            ProxyType: proxyType,
            Address: address,
            Username: null,
            Password: null,
            IsEnabled: true,
            CreatedUtc: now,
            UpdatedUtc: now);
    }
}
