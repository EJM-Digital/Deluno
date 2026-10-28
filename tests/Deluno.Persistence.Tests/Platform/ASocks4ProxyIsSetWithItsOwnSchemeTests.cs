using System.Net;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>#1301: a SOCKS4 indexer proxy builds a handler that speaks SOCKS4 (not the SOCKS5 or HTTP scheme), with its credentials, and a different proxy of the same kind gets its own handler.</summary>
public sealed class ASocks4ProxyIsSetWithItsOwnSchemeTests
{
    private static ProxyItem Proxy(string id, string address, string? username = null, string? password = null)
        => new(id, "SOCKS4 proxy", ProxyTypes.Socks4, address, username, password, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public void A_socks4_proxy_is_set_on_the_handler_with_the_socks4_scheme_and_the_credentials_given()
    {
        using var factory = new IndexerProxyHttpClientFactory();

        var handler = Assert.IsType<SocketsHttpHandler>(factory.GetHandler(Proxy("proxy-4", "192.168.1.4:1080", "user", "pass")));

        var webProxy = Assert.IsType<WebProxy>(handler.Proxy);
        Assert.Equal("socks4://192.168.1.4:1080/", webProxy.Address!.ToString());
        Assert.True(handler.UseProxy);
        var credential = Assert.IsType<NetworkCredential>(webProxy.Credentials);
        Assert.Equal("user", credential.UserName);
        Assert.Equal("pass", credential.Password);
    }

    [Fact]
    public void Two_socks4_proxies_get_their_own_handlers_and_the_same_one_is_reused()
    {
        using var factory = new IndexerProxyHttpClientFactory();
        var first = Proxy("a", "192.168.1.4:1080");

        Assert.Same(factory.GetHandler(first), factory.GetHandler(first));
        Assert.NotSame(factory.GetHandler(first), factory.GetHandler(Proxy("b", "192.168.1.5:1080")));
    }
}
