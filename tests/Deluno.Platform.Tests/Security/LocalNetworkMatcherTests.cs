using System.Net;
using Deluno.Security;

namespace Deluno.Platform.Tests.Security;

public sealed class LocalNetworkMatcherTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.20")]
    [InlineData("169.254.1.1")]
    [InlineData("::1")]
    [InlineData("fd12:3456:789a::1")] // IPv6 ULA (fc00::/7)
    [InlineData("fe80::1")] // IPv6 link-local
    public void Built_in_local_ranges_are_local(string address)
    {
        Assert.True(LocalNetworkMatcher.IsLocal(IPAddress.Parse(address), []));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.32.0.1")] // just outside 172.16.0.0/12
    [InlineData("172.15.255.255")] // just outside 172.16.0.0/12
    [InlineData("2001:4860:4860::8888")] // public IPv6
    public void Public_addresses_are_not_local(string address)
    {
        Assert.False(LocalNetworkMatcher.IsLocal(IPAddress.Parse(address), []));
    }

    [Fact]
    public void A_person_added_network_counts_as_local_too()
    {
        Assert.True(LocalNetworkMatcher.IsLocal(IPAddress.Parse("203.0.113.5"), ["203.0.113.0/24"]));
        Assert.False(LocalNetworkMatcher.IsLocal(IPAddress.Parse("203.0.114.5"), ["203.0.113.0/24"]));
    }

    [Theory]
    [InlineData("192.168.1.20", true)]
    [InlineData("192.168.1.20/32", true)]
    [InlineData("192.168.1.0/24", true)]
    [InlineData("fd00::/8", true)]
    [InlineData("not-an-address", false)]
    [InlineData("192.168.1.0/33", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Entry_validity(string? entry, bool expected)
    {
        Assert.Equal(expected, LocalNetworkMatcher.IsValidEntry(entry));
    }

    [Fact]
    public void MatchesAny_checks_membership_of_a_cidr_list()
    {
        Assert.True(LocalNetworkMatcher.MatchesAny(IPAddress.Parse("192.168.1.20"), ["192.168.1.16/28"]));
        Assert.False(LocalNetworkMatcher.MatchesAny(IPAddress.Parse("192.168.1.20"), ["192.168.1.32/28"]));
    }

    [Fact]
    public void An_ipv4_mapped_ipv6_address_matches_the_equivalent_ipv4_entry()
    {
        var mapped = IPAddress.Parse("::ffff:192.168.1.20");
        Assert.True(LocalNetworkMatcher.IsLocal(mapped, []));
        Assert.True(LocalNetworkMatcher.MatchesAny(mapped, ["192.168.1.20"]));
    }
}
