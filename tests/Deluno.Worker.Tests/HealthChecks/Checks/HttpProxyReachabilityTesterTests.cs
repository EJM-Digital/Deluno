using System.Net;
using System.Net.Sockets;
using Deluno.Connections.Contracts;
using Deluno.Worker.HealthChecks;
using Deluno.Worker.HealthChecks.Checks;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>
/// The real reachability test #868 asks for: a forward proxy (HTTP/SOCKS4/
/// SOCKS5) is tested by a plain TCP connect to its own host and port -- the
/// same connection any of those protocols needs before anything else can
/// happen -- rather than by routing a request to some third-party address,
/// which would make the result depend on that address's own uptime as much
/// as the proxy's.
/// </summary>
public sealed class HttpProxyReachabilityTesterTests
{
    [Fact]
    public async Task A_forward_proxy_listening_on_its_configured_port_is_reachable()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptTcpClientAsync();

        var tester = CreateTester();
        var proxy = Proxy(ProxyTypes.Socks5, $"socks5://127.0.0.1:{port}");

        var result = await tester.TestAsync(proxy, CancellationToken.None);

        Assert.True(result.Reachable);
        Assert.Contains(proxy.Name, result.Message);
        (await acceptTask).Dispose();
    }

    [Fact]
    public async Task A_forward_proxy_with_nothing_listening_is_not_reachable()
    {
        var tester = CreateTester();
        // Port 1 is reserved and nothing binds to it in a test sandbox.
        var proxy = Proxy(ProxyTypes.Http, "http://127.0.0.1:1");

        var result = await tester.TestAsync(proxy, CancellationToken.None);

        Assert.False(result.Reachable);
        Assert.Contains(proxy.Name, result.Message);
    }

    [Fact]
    public async Task A_forward_proxy_with_an_unusable_address_is_not_reachable()
    {
        var tester = CreateTester();
        var proxy = Proxy(ProxyTypes.Http, "   ");

        var result = await tester.TestAsync(proxy, CancellationToken.None);

        Assert.False(result.Reachable);
    }

    [Fact]
    public void A_forward_proxy_has_ten_seconds_to_accept_the_connection()
        => Assert.Equal(TimeSpan.FromSeconds(10), HttpProxyReachabilityTester.DefaultConnectTimeout);

    [Fact]
    public async Task A_forward_proxy_that_does_not_accept_in_time_is_reported_as_timed_out_not_as_refused()
    {
        // A zero allowance has already run out before the connect starts: the same branch a proxy that never answers takes after the ten seconds.
        var services = new ServiceCollection();
        services.AddHttpClient(HealthCheckHttpClients.Proxy);
        var tester = new HttpProxyReachabilityTester(services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>(), TimeSpan.Zero);

        var result = await tester.TestAsync(Proxy(ProxyTypes.Http, "http://127.0.0.1:8123"), CancellationToken.None);

        Assert.False(result.Reachable);
        Assert.Contains("did not accept a connection at 127.0.0.1:8123 within 0 seconds", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ProxyTypes.Http, "127.0.0.1", ":8080")]
    [InlineData(ProxyTypes.Http, "http://127.0.0.1", ":8080")]
    [InlineData(ProxyTypes.Socks5, "127.0.0.1", ":1080")]
    [InlineData(ProxyTypes.Socks4, "127.0.0.1", ":1080")]
    [InlineData(ProxyTypes.Socks5, "socks5://127.0.0.1", ":1080")]
    public async Task A_forward_proxy_address_with_no_port_is_tested_on_the_usual_port_for_its_kind(string type, string address, string expectedPort)
    {
        var result = await CreateTester().TestAsync(Proxy(type, address), CancellationToken.None);

        // Whether or not something happens to listen there, the test names the host and port it actually tried.
        Assert.Contains("127.0.0.1" + expectedPort, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_flaresolverr_proxy_with_a_blank_address_is_not_reachable()
    {
        var tester = CreateTester();
        var proxy = Proxy(ProxyTypes.FlareSolverr, " ");

        var result = await tester.TestAsync(proxy, CancellationToken.None);

        Assert.False(result.Reachable);
        Assert.Contains("no address", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_flaresolverr_proxy_nothing_answers_at_is_not_reachable()
    {
        var tester = CreateTester();
        // No FlareSolverr helper is listening on this loopback port.
        var proxy = Proxy(ProxyTypes.FlareSolverr, "http://127.0.0.1:1");

        var result = await tester.TestAsync(proxy, CancellationToken.None);

        Assert.False(result.Reachable);
    }

    private static HttpProxyReachabilityTester CreateTester()
    {
        var services = new ServiceCollection();
        services.AddHttpClient(HealthCheckHttpClients.Proxy, client => client.Timeout = TimeSpan.FromSeconds(3));
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
