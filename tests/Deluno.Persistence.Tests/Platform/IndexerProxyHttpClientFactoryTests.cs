using System.Net;
using System.Net.Http;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// #976: every HTTP request an indexer makes for itself -- search, RSS, caps
/// and (for a native tracker) a grab -- must go through the HTTP/SOCKS proxy
/// assigned to it, built once per proxy id and reused rather than a fresh
/// (and never a shared global) proxy for every request.
/// </summary>
public sealed class IndexerProxyHttpClientFactoryTests
{
    [Fact]
    public void An_http_proxy_is_set_on_the_handler_it_builds()
    {
        var proxy = Proxy("proxy-1", ProxyTypes.Http, "192.168.1.1:8080", "user", "pass");
        var factory = new IndexerProxyHttpClientFactory();

        var handler = Assert.IsType<SocketsHttpHandler>(factory.GetHandler(proxy));

        var webProxy = Assert.IsType<WebProxy>(handler.Proxy);
        Assert.Equal("http://192.168.1.1:8080/", webProxy.Address!.ToString());
        Assert.True(handler.UseProxy);
        var credential = Assert.IsType<NetworkCredential>(webProxy.Credentials);
        Assert.Equal("user", credential.UserName);
        Assert.Equal("pass", credential.Password);
    }

    [Fact]
    public void A_socks5_proxy_is_set_with_its_own_scheme()
    {
        var proxy = Proxy("proxy-2", ProxyTypes.Socks5, "192.168.1.2:1080", null, null);
        var factory = new IndexerProxyHttpClientFactory();

        var handler = Assert.IsType<SocketsHttpHandler>(factory.GetHandler(proxy));

        var webProxy = Assert.IsType<WebProxy>(handler.Proxy);
        Assert.Equal("socks5://192.168.1.2:1080/", webProxy.Address!.ToString());
    }

    [Fact]
    public void No_proxy_assigned_builds_a_plain_direct_handler()
    {
        var factory = new IndexerProxyHttpClientFactory();

        var handler = Assert.IsType<SocketsHttpHandler>(factory.GetHandler(null));

        Assert.Null(handler.Proxy);
        Assert.False(handler.UseProxy);
    }

    /// <summary>
    /// #1027/#976: an indexer with no proxy assigned must connect directly --
    /// never an ambient HTTP_PROXY/HTTPS_PROXY environment variable or a
    /// WinINET/WinHTTP system proxy left over on the host, the same leftover
    /// that could silently reroute a tracker request to a dead address.
    /// UseProxy is only ever true when this factory was given an indexer's
    /// own assigned HTTP/SOCKS proxy.
    /// </summary>
    [Fact]
    public void No_proxy_assigned_never_uses_an_ambient_system_or_environment_proxy()
    {
        var factory = new IndexerProxyHttpClientFactory();

        var handler = Assert.IsType<SocketsHttpHandler>(factory.GetHandler(null));

        Assert.False(handler.UseProxy);
        Assert.Null(handler.Proxy);
    }

    [Fact]
    public void A_flaresolverr_proxy_is_a_cloudflare_helper_not_a_forward_proxy_so_it_builds_the_direct_handler()
    {
        // #976: assigning a FlareSolverr/Byparr instance means this indexer's
        // requests go through it as a Cloudflare helper, not that Deluno's
        // own plain requests are routed through it as a forward proxy --
        // there is no such protocol.
        var proxy = Proxy("proxy-3", ProxyTypes.FlareSolverr, "127.0.0.1:8191", null, null);
        var factory = new IndexerProxyHttpClientFactory();

        var handler = Assert.IsType<SocketsHttpHandler>(factory.GetHandler(proxy));

        Assert.Null(handler.Proxy);
        Assert.False(handler.UseProxy);
    }

    [Fact]
    public void A_disabled_proxy_builds_the_direct_handler()
    {
        var proxy = Proxy("proxy-4", ProxyTypes.Http, "192.168.1.4:8080", null, null, isEnabled: false);
        var factory = new IndexerProxyHttpClientFactory();

        var handler = Assert.IsType<SocketsHttpHandler>(factory.GetHandler(proxy));

        Assert.Null(handler.Proxy);
    }

    [Fact]
    public void The_same_proxy_id_reuses_the_same_handler()
    {
        var proxy = Proxy("proxy-1", ProxyTypes.Http, "192.168.1.1:8080", null, null);
        var factory = new IndexerProxyHttpClientFactory();

        var first = factory.GetHandler(proxy);
        var second = factory.GetHandler(proxy with { Name = "Renamed" });

        Assert.Same(first, second);
    }

    [Fact]
    public void Two_indexers_on_different_proxies_get_different_handlers_and_one_indexer_is_unaffected_by_the_others_proxy()
    {
        var proxyA = Proxy("proxy-a", ProxyTypes.Http, "10.0.0.1:8080", null, null);
        var proxyB = Proxy("proxy-b", ProxyTypes.Http, "10.0.0.2:9090", null, null);
        var factory = new IndexerProxyHttpClientFactory();

        var handlerA = Assert.IsType<SocketsHttpHandler>(factory.GetHandler(proxyA));
        var handlerB = Assert.IsType<SocketsHttpHandler>(factory.GetHandler(proxyB));
        var handlerDirect = Assert.IsType<SocketsHttpHandler>(factory.GetHandler(null));

        Assert.NotSame(handlerA, handlerB);
        Assert.NotSame(handlerA, handlerDirect);
        Assert.Equal("http://10.0.0.1:8080/", ((WebProxy)handlerA.Proxy!).Address!.ToString());
        Assert.Equal("http://10.0.0.2:9090/", ((WebProxy)handlerB.Proxy!).Address!.ToString());
        Assert.Null(handlerDirect.Proxy);
    }

    [Fact]
    public void CreateClient_wraps_the_cached_handler_in_a_fresh_client_each_time()
    {
        var proxy = Proxy("proxy-1", ProxyTypes.Http, "192.168.1.1:8080", null, null);
        var factory = new IndexerProxyHttpClientFactory();

        using var first = factory.CreateClient(proxy);
        using var second = factory.CreateClient(proxy);

        Assert.NotSame(first, second);
    }

    private static ProxyItem Proxy(string id, string type, string address, string? username, string? password, bool isEnabled = true)
        => new(id, "Test Proxy", type, address, username, password, isEnabled, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
