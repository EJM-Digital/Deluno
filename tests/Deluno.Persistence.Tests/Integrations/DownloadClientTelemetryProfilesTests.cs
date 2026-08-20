using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Persistence.Tests.Integrations;

public sealed class DownloadClientTelemetryProfilesTests
{
    [Theory]
    [InlineData("qbittorrent", false, true, true, "form")]
    [InlineData("sabnzbd", true, true, false, "api-key")]
    [InlineData("nzbget", true, true, false, "basic")]
    [InlineData("transmission", false, true, true, "basic")]
    [InlineData("deluge", false, true, true, "password")]
    [InlineData("utorrent", false, false, true, "basic-token")]
    public void ResolveCapabilities_ReturnsExpectedProtocolSupport(
        string protocol,
        bool supportsHistory,
        bool supportsImportPath,
        bool supportsRecheck,
        string authMode)
    {
        Assert.True(Registry().TryGet(protocol, out var client));
        var capabilities = client.Capabilities;

        Assert.True(capabilities.SupportsQueue);
        Assert.Equal(supportsHistory, capabilities.SupportsHistory);
        Assert.True(capabilities.SupportsPauseResume);
        Assert.True(capabilities.SupportsRemove);
        Assert.Equal(supportsRecheck, capabilities.SupportsRecheck);
        Assert.Equal(supportsImportPath, capabilities.SupportsImportPath);
        Assert.Equal(authMode, capabilities.AuthMode);
    }

    [Fact]
    public void Registry_rejects_unknown_protocols_and_lists_supported_protocols()
    {
        var registry = Registry();

        Assert.False(registry.TryGet("custom", out _));
        Assert.False(registry.TryGet("nonsense", out _));
        Assert.Equal(["deluge", "nzbget", "qbittorrent", "sabnzbd", "transmission", "utorrent"], registry.KnownProtocols);
    }

    [Theory]
    [InlineData("qbittorrent", "downloading", 0.42, null, null, DownloadQueueStatuses.Downloading)]
    [InlineData("qbittorrent", "queuedDL", 0.0, null, null, DownloadQueueStatuses.Queued)]
    [InlineData("qbittorrent", "stalledDL", 0.5, null, null, DownloadQueueStatuses.Stalled)]
    [InlineData("qbittorrent", "uploading", 1.0, null, null, DownloadQueueStatuses.ImportReady)]
    [InlineData("sabnzbd", "Paused", 12.0, null, null, DownloadQueueStatuses.Queued)]
    [InlineData("sabnzbd", "Downloading", 50.0, null, null, DownloadQueueStatuses.Downloading)]
    [InlineData("sabnzbd", "Completed", 100.0, null, null, DownloadQueueStatuses.ImportReady)]
    [InlineData("nzbget", "ERROR", 33.0, null, null, DownloadQueueStatuses.Stalled)]
    [InlineData("deluge", "Seeding", 100.0, null, null, DownloadQueueStatuses.ImportReady)]
    [InlineData("utorrent", "Queued", 12.0, null, null, DownloadQueueStatuses.Queued)]
    [InlineData("transmission", "4", 0.2, null, null, DownloadQueueStatuses.Downloading)]
    [InlineData("transmission", "0", 0.0, null, null, DownloadQueueStatuses.Queued)]
    [InlineData("transmission", "4", 1.0, null, null, DownloadQueueStatuses.ImportReady)]
    [InlineData("transmission", "4", 0.5, 3, "tracker error", DownloadQueueStatuses.Stalled)]
    public void NormalizeStatus_MapsClientStatesToCanonicalQueueStatus(
        string protocol,
        string nativeStatus,
        double progress,
        int? errorCode,
        string? errorMessage,
        string expected)
    {
        Assert.True(Registry().TryGet(protocol, out var client));
        var status = client.NormalizeStatus(nativeStatus, progress, errorCode, errorMessage);

        Assert.Equal(expected, status);
    }

    private static IDownloadClientRegistry Registry()
        => new DownloadClientRegistry(
        [
            new QbittorrentDownloadClient(),
            new SabnzbdDownloadClient(null!),
            new NzbGetDownloadClient(null!),
            new TransmissionDownloadClient(null!),
            new DelugeDownloadClient(null!),
            new UTorrentDownloadClient()
        ]);
}
