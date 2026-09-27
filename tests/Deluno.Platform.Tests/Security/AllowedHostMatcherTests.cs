using Deluno.Security;

namespace Deluno.Platform.Tests.Security;

public sealed class AllowedHostMatcherTests
{
    [Theory]
    [InlineData("localhost")]
    [InlineData("localhost:5099")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:5099")]
    [InlineData("[::1]")]
    [InlineData("[::1]:5099")]
    public void Loopback_addresses_are_always_allowed(string host)
    {
        Assert.True(AllowedHostMatcher.IsAlwaysAllowedLoopbackHost(host));
    }

    [Theory]
    [InlineData("evil.example.com")]
    [InlineData("192.168.1.10")]
    [InlineData("my-pc")]
    public void Non_loopback_addresses_are_not_always_allowed(string host)
    {
        Assert.False(AllowedHostMatcher.IsAlwaysAllowedLoopbackHost(host));
    }

    [Fact]
    public void Exact_match_is_case_insensitive_and_ignores_port()
    {
        Assert.True(AllowedHostMatcher.Matches("My-PC:5099", ["my-pc"]));
    }

    [Fact]
    public void Unlisted_host_does_not_match()
    {
        Assert.False(AllowedHostMatcher.Matches("evil.example.com", ["my-pc", "localhost"]));
    }

    [Fact]
    public void Wildcard_matches_any_subdomain()
    {
        Assert.True(AllowedHostMatcher.Matches("deluno.example.com", ["*.example.com"]));
        Assert.True(AllowedHostMatcher.Matches("a.b.example.com", ["*.example.com"]));
    }

    [Fact]
    public void Wildcard_does_not_match_the_bare_domain_itself()
    {
        Assert.False(AllowedHostMatcher.Matches("example.com", ["*.example.com"]));
    }

    [Fact]
    public void Wildcard_does_not_match_an_unrelated_domain()
    {
        Assert.False(AllowedHostMatcher.Matches("evil.com", ["*.example.com"]));
    }
}
