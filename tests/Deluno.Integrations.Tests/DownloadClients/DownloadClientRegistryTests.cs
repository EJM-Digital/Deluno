using Deluno.Integrations.DownloadClients;
using Moq;

namespace Deluno.Integrations.Tests.DownloadClients;

public sealed class DownloadClientRegistryTests
{
    private static IDownloadClient Client(string protocol)
    {
        var client = new Mock<IDownloadClient>();
        client.SetupGet(c => c.Protocol).Returns(protocol);
        return client.Object;
    }

    [Fact]
    public void The_known_protocols_are_listed_in_order_and_a_client_is_found_by_protocol_ignoring_case_and_spaces()
    {
        var qbit = Client("qbittorrent");
        var sab = Client("sabnzbd");
        var registry = new DownloadClientRegistry([sab, qbit]);

        Assert.Equal(["qbittorrent", "sabnzbd"], registry.KnownProtocols);
        Assert.True(registry.TryGet("QBittorrent", out var found));
        Assert.Same(qbit, found);
        Assert.True(registry.TryGet("  sabnzbd ", out found));
        Assert.Same(sab, found);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("utorrent")]
    public void An_unknown_or_blank_protocol_finds_no_client(string? protocol)
        => Assert.False(new DownloadClientRegistry([Client("qbittorrent")]).TryGet(protocol, out _));
}
