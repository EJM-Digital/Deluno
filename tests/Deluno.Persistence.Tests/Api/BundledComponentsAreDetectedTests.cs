using Deluno.Api.Network;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Detection is what lets the installer's component picker skip a client
/// already on the machine, and lets the setup wizard offer a tested
/// connection instead of an empty host/port form (#640).
/// </summary>
public sealed class BundledComponentsAreDetectedTests
{
    [Fact]
    public async Task Every_known_component_is_reported_even_when_nothing_is_installed()
    {
        var detection = new WindowsBundledComponentDetection(new FakePrograms(), new FakePorts());

        var status = await detection.DetectAsync(CancellationToken.None);

        Assert.Equal(6, status.Count);
        Assert.Contains(status, item => item.Kind == "qbittorrent");
        Assert.Contains(status, item => item.Kind == "transmission");
        Assert.Contains(status, item => item.Kind == "deluge");
        Assert.Contains(status, item => item.Kind == "sabnzbd");
        Assert.Contains(status, item => item.Kind == "nzbget");
        Assert.Contains(status, item => item.Kind == "weir");
        Assert.All(status, item => Assert.False(item.Installed));
        Assert.All(status, item => Assert.False(item.Reachable));
    }

    [Fact]
    public async Task An_uninstall_entry_is_read_as_installed_by_a_substring_match()
    {
        var detection = new WindowsBundledComponentDetection(
            new FakePrograms("qBittorrent 5.2.1"),
            new FakePorts());

        var status = await detection.DetectAsync(CancellationToken.None);

        var qbittorrent = status.Single(item => item.Kind == "qbittorrent");
        Assert.True(qbittorrent.Installed);
        var others = status.Where(item => item.Kind != "qbittorrent");
        Assert.All(others, item => Assert.False(item.Installed));
    }

    [Fact]
    public async Task An_answering_default_port_is_reported_reachable()
    {
        var detection = new WindowsBundledComponentDetection(
            new FakePrograms(),
            new FakePorts(open: 9347));

        var status = await detection.DetectAsync(CancellationToken.None);

        var weir = status.Single(item => item.Kind == "weir");
        Assert.True(weir.Reachable);
        Assert.Equal("localhost", weir.DefaultHost);
        Assert.Equal(9347, weir.DefaultPort);
        var others = status.Where(item => item.Kind != "weir");
        Assert.All(others, item => Assert.False(item.Reachable));
    }

    [Fact]
    public async Task A_host_with_no_detection_reports_every_component_as_unknown()
    {
        var status = await new UncheckedBundledComponentDetection().DetectAsync(CancellationToken.None);

        Assert.Equal(6, status.Count);
        Assert.All(status, item => Assert.False(item.Installed));
        Assert.All(status, item => Assert.False(item.Reachable));
    }

    [Fact]
    public void The_download_client_kinds_match_what_Deluno_can_actually_dispatch_to()
    {
        // #640's picker offers exactly the five clients Deluno can send a
        // release to, plus Weir. Ticking a client the dispatcher does not
        // recognise would install software Deluno can never use.
        var downloadClientKinds = BundledComponentCatalog.All
            .Where(spec => spec.Category == BundledComponentCatalog.DownloadClientCategory)
            .Select(spec => spec.Kind)
            .ToArray();

        Assert.Equal(new[] { "qbittorrent", "transmission", "deluge", "sabnzbd", "nzbget" }, downloadClientKinds);
    }

    private sealed class FakePrograms(params string[] names) : IInstalledProgramsReader
    {
        public IReadOnlyList<string> ReadDisplayNames() => names;
    }

    private sealed class FakePorts(int? open = null) : IPortProbe
    {
        public Task<bool> IsOpenAsync(string host, int port, CancellationToken cancellationToken)
            => Task.FromResult(open == port);
    }
}
