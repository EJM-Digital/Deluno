using System.Net;
using Deluno.Security.Contracts;
using Deluno.Security.Hardening;
using Microsoft.AspNetCore.Http;

namespace Deluno.Platform.Tests.Security.Hardening;

public sealed class TrustedProxyClientAddressResolverTests
{
    private sealed class FakeCache(LocalNetworkAuthSettings settings) : ILocalNetworkAuthSettingsCache
    {
        public Task<LocalNetworkAuthSettings> GetAsync(CancellationToken cancellationToken) => Task.FromResult(settings);

        public void Invalidate()
        {
        }
    }

    private static DefaultHttpContext ContextFor(string remoteAddress, string? forwardedFor = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteAddress);
        if (forwardedFor is not null)
        {
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        }

        return context;
    }

    [Fact]
    public async Task No_trusted_proxies_configured_uses_the_connection_address_as_is()
    {
        var resolver = new TrustedProxyClientAddressResolver(new FakeCache(new LocalNetworkAuthSettings(true, [], [], [])));
        var context = ContextFor("192.168.1.20", "10.0.0.1");

        var resolved = await resolver.ResolveAsync(context, CancellationToken.None);

        Assert.Equal(IPAddress.Parse("192.168.1.20"), resolved);
    }

    [Fact]
    public async Task Spoofed_forwarded_for_from_an_untrusted_proxy_is_ignored()
    {
        // The connection itself came from a public address that is not on
        // the trusted-proxies list, so its claimed X-Forwarded-For (trying
        // to pose as a local client) must never be honored.
        var settings = new LocalNetworkAuthSettings(true, [], [], ["10.0.0.5"]);
        var resolver = new TrustedProxyClientAddressResolver(new FakeCache(settings));
        var context = ContextFor("8.8.8.8", "127.0.0.1");

        var resolved = await resolver.ResolveAsync(context, CancellationToken.None);

        Assert.Equal(IPAddress.Parse("8.8.8.8"), resolved);
    }

    [Fact]
    public async Task A_trusted_proxy_hands_off_the_real_client_address()
    {
        var settings = new LocalNetworkAuthSettings(true, [], [], ["10.0.0.5"]);
        var resolver = new TrustedProxyClientAddressResolver(new FakeCache(settings));
        var context = ContextFor("10.0.0.5", "192.168.1.50");

        var resolved = await resolver.ResolveAsync(context, CancellationToken.None);

        Assert.Equal(IPAddress.Parse("192.168.1.50"), resolved);
    }

    [Fact]
    public async Task Walks_multiple_trusted_hops_and_stops_at_the_first_untrusted_one()
    {
        var settings = new LocalNetworkAuthSettings(true, [], [], ["10.0.0.5", "10.0.0.6"]);
        var resolver = new TrustedProxyClientAddressResolver(new FakeCache(settings));
        // Nearest-first order as the header actually carries it: the real
        // client, then each trusted proxy it passed through.
        var context = ContextFor("10.0.0.5", "192.168.1.50, 10.0.0.6");

        var resolved = await resolver.ResolveAsync(context, CancellationToken.None);

        Assert.Equal(IPAddress.Parse("192.168.1.50"), resolved);
    }

    [Fact]
    public async Task No_forwarded_header_from_a_trusted_proxy_uses_its_own_address()
    {
        var settings = new LocalNetworkAuthSettings(true, [], [], ["10.0.0.5"]);
        var resolver = new TrustedProxyClientAddressResolver(new FakeCache(settings));
        var context = ContextFor("10.0.0.5");

        var resolved = await resolver.ResolveAsync(context, CancellationToken.None);

        Assert.Equal(IPAddress.Parse("10.0.0.5"), resolved);
    }
}
