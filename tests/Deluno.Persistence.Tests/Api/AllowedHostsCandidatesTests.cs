using System.Net.NetworkInformation;
using System.Net.Sockets;
using Deluno.Security.Hardening;
using Microsoft.AspNetCore.Http;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The addresses the Allowed Hosts card offers as tick boxes (#939): the one in use, this machine's name, its LAN addresses,
/// localhost and the hosts a reverse proxy was seen using. Detected from this computer's own network adapters, never typed.
/// </summary>
public sealed class AllowedHostsCandidatesTests
{
    private static HttpContext RequestFor(string host)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        return context;
    }

    [Fact]
    public void Every_LAN_address_offered_is_a_real_IPv4_address_of_an_adapter_that_is_up_here()
    {
        var provider = new AllowedHostsCandidateProvider(new ForwardedHostTracker());

        var offered = provider.DetectCandidates(RequestFor("deluno.lan:8080"))
            .Where(candidate => candidate.Reason == "A LAN address of this computer")
            .Select(candidate => candidate.Host)
            .ToArray();

        var real = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Where(unicast => unicast.Address.AddressFamily == AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(unicast.Address))
            .Select(unicast => unicast.Address.ToString())
            .Distinct()
            .OrderBy(address => address)
            .ToArray();

        // Exactly the addresses this computer has: none missing, none invented, none twice.
        Assert.Equal(real, offered.Distinct().OrderBy(address => address).ToArray());
        Assert.Equal(offered.Length, offered.Distinct().Count());
    }

    [Fact]
    public void A_host_a_reverse_proxy_was_seen_using_is_offered_with_that_reason_and_not_twice()
    {
        var tracker = new ForwardedHostTracker();
        tracker.Record("media.example.net:443");
        tracker.Record("media.example.net");
        var provider = new AllowedHostsCandidateProvider(tracker);

        var offered = provider.DetectCandidates(RequestFor("deluno.lan:8080"));

        var forwarded = Assert.Single(offered, candidate => candidate.Host == "media.example.net");
        Assert.Equal("Seen in a request forwarded from a reverse proxy or Docker host", forwarded.Reason);
    }

    [Fact]
    public void The_address_in_use_comes_first_with_its_port_left_off_and_is_not_offered_again_as_localhost_or_a_LAN_address()
    {
        var provider = new AllowedHostsCandidateProvider(new ForwardedHostTracker());

        var offered = provider.DetectCandidates(RequestFor("localhost:5099"));

        Assert.Equal("localhost", offered[0].Host);
        Assert.Equal("The address you're using right now", offered[0].Reason);
        Assert.Single(offered, candidate => candidate.Host == "localhost");
        Assert.Contains(offered, candidate => candidate.Host == "127.0.0.1" && candidate.Reason == "Always answers from this machine");
        Assert.Contains(offered, candidate => candidate.Host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) && candidate.Reason == "This computer's name");
    }

    [Fact]
    public void With_no_request_in_hand_the_rest_are_still_offered()
    {
        var provider = new AllowedHostsCandidateProvider(new ForwardedHostTracker());

        var offered = provider.DetectCandidates(null);

        Assert.DoesNotContain(offered, candidate => candidate.Reason == "The address you're using right now");
        Assert.Contains(offered, candidate => candidate.Host == "localhost");
    }
}
