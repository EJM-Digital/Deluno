using Deluno.Integrations.DownloadClients;

namespace Deluno.Persistence.Tests.Integrations;

public sealed class DownloadClientTelemetryProfilesTests
{
    [Theory]
    [InlineData("qbittorrent", true, true, true, "form")]
    [InlineData("sabnzbd", true, true, false, "api-key")]
    [InlineData("nzbget", true, true, false, "basic")]
    [InlineData("transmission", true, true, true, "basic")]
    [InlineData("deluge", true, true, true, "password")]
    [InlineData("utorrent", true, false, true, "basic-token")]
    public void ResolveCapabilities_ReturnsExpectedProtocolSupport(
        string protocol,
        bool supportsHistory,
        bool supportsImportPath,
        bool supportsRecheck,
        string authMode)
    {
        var capabilities = DownloadClientTelemetryProfiles.ResolveCapabilities(protocol);

        Assert.True(capabilities.SupportsQueue);
        Assert.Equal(supportsHistory, capabilities.SupportsHistory);
        Assert.True(capabilities.SupportsPauseResume);
        Assert.True(capabilities.SupportsRemove);
        Assert.Equal(supportsRecheck, capabilities.SupportsRecheck);
        Assert.Equal(supportsImportPath, capabilities.SupportsImportPath);
        Assert.Equal(authMode, capabilities.AuthMode);
    }

    [Fact]
    public void ResolveCapabilities_ReturnsClosedProfileForUnknownProtocol()
    {
        var capabilities = DownloadClientTelemetryProfiles.ResolveCapabilities("custom");

        Assert.False(capabilities.SupportsQueue);
        Assert.False(capabilities.SupportsHistory);
        Assert.False(capabilities.SupportsPauseResume);
        Assert.False(capabilities.SupportsRemove);
        Assert.False(capabilities.SupportsRecheck);
        Assert.False(capabilities.SupportsImportPath);
        Assert.Equal("unknown", capabilities.AuthMode);
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
        var status = DownloadClientTelemetryProfiles.NormalizeStatus(
            protocol,
            nativeStatus,
            progress,
            errorCode,
            errorMessage);

        Assert.Equal(expected, status);
    }
}
