using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Recovery.Services;
using Deluno.Series.Data;
using Deluno.Worker.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// #817: the download-health ignore list (blocked by hash, category, or
/// tracker domain) exempts a download from every health rule — strikes,
/// blocklisting, and, since #817's own acceptance line, the seeding reclaim
/// pass too (<see cref="WorkPlanner.PlanSharingReclaimAsync"/>). A matching
/// entry must stop the pass from ever asking the client to remove or stop
/// seeding the item, and from recording it as reclaimed.
/// </summary>
public sealed class IgnoredDownloadSkipsSeedingReclaimTests
{
    private sealed class RecordingGateway : IDownloadClientActionGateway
    {
        public List<string> RemoveCalls { get; } = [];
        public List<string> StopCalls { get; } = [];

        public Task<DownloadClientRemovalResult> RemoveWithDataAsync(string clientId, string queueItemId, CancellationToken cancellationToken)
        {
            RemoveCalls.Add(queueItemId);
            return Task.FromResult(new DownloadClientRemovalResult(true, "Removed."));
        }

        public Task<DownloadClientRemovalResult> StopSeedingAsync(string clientId, string queueItemId, CancellationToken cancellationToken)
        {
            StopCalls.Add(queueItemId);
            return Task.FromResult(new DownloadClientRemovalResult(true, "Stopped."));
        }
    }

    [Theory]
    // Ignored by the exact download hash/client item id.
    [InlineData("item-1", null, null)]
    // Ignored by the download's own category.
    [InlineData(null, "usenet-movies", null)]
    // Ignored by the tracker/indexer domain.
    [InlineData(null, null, "example.tracker")]
    public async Task An_ignored_download_is_left_alone_by_the_seeding_reclaim_pass(string? hash, string? category, string? trackerDomain)
    {
        var gateway = new RecordingGateway();
        var activity = new Mock<IActivityFeedRepository>();

        await Run(gateway, activity, ignoreEntry: new DownloadHealthIgnoreEntry("ignore-1", hash, category, trackerDomain, null, DateTimeOffset.UnixEpoch));

        Assert.Empty(gateway.RemoveCalls);
        Assert.Empty(gateway.StopCalls);
        activity.Verify(
            repository => repository.RecordActivityAsync(
                It.Is<string>(type => type.StartsWith("download.sharing.", StringComparison.Ordinal)),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// The control: the very same download, with no matching ignore entry, is
    /// reclaimed. Without this, a pass that (by defect) skipped everything
    /// would pass the test above too.
    /// </summary>
    [Fact]
    public async Task The_same_download_is_reclaimed_when_nothing_ignores_it()
    {
        var gateway = new RecordingGateway();
        var activity = new Mock<IActivityFeedRepository>();

        await Run(gateway, activity, ignoreEntry: null);

        Assert.Equal(["item-1"], gateway.RemoveCalls);
    }

    private static async Task Run(RecordingGateway gateway, Mock<IActivityFeedRepository> activity, DownloadHealthIgnoreEntry? ignoreEntry)
    {
        var jobs = new Mock<IJobQueueRepository>();
        jobs.Setup(repository => repository.TryClaimScheduledPassAsync(
                It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        jobs.Setup(repository => repository.FindDispatchLinkByClientItemAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchCatalogueLink("dispatch-1", "movie", "movie-1", IndexerName: "A tracker", LibraryId: "lib-1"));

        var queueItem = new DownloadQueueItem(
            Id: "item-1",
            ClientId: "client-1",
            ClientName: "SABnzbd",
            Protocol: DownloadProtocols.Usenet,
            MediaType: "movie",
            Title: "Arrival",
            ReleaseName: "Arrival.2016.WEB.1080p-GROUP",
            Category: "usenet-movies",
            Status: DownloadQueueStatuses.Imported,
            Progress: 100,
            SpeedMbps: 0,
            EtaSeconds: 0,
            SizeBytes: 4_000_000_000,
            DownloadedBytes: 4_000_000_000,
            Peers: 0,
            IndexerName: "example.tracker",
            ErrorMessage: null,
            AddedUtc: DateTimeOffset.UnixEpoch);

        var snapshot = new DownloadClientTelemetrySnapshot(
            ClientId: "client-1",
            ClientName: "SABnzbd",
            Protocol: DownloadProtocols.Usenet,
            EndpointUrl: null,
            HealthStatus: "healthy",
            LastHealthMessage: null,
            Capabilities: new DownloadClientTelemetryCapabilities(true, true, true, true, false, false, "none"),
            Summary: new DownloadTelemetrySummary(0, 0, 1, 0, 0, 0, 0),
            Queue: [queueItem],
            History: [],
            CapturedUtc: DateTimeOffset.UnixEpoch);

        var telemetry = new Mock<IDownloadClientTelemetryService>();
        telemetry.Setup(service => service.GetOverviewAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DownloadTelemetryOverview(
                new DownloadTelemetrySummary(0, 0, 1, 0, 0, 0, 0),
                [snapshot],
                DateTimeOffset.UnixEpoch));

        var platformSettings = new Mock<IPlatformSettingsRepository>();
        platformSettings.Setup(repository => repository.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultSettings());

        var connections = new Mock<IConnectionsRepository>();
        connections.Setup(repository => repository.ListIndexersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repository => repository.ListLibrariesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var sharing = new Mock<IDownloadSharingRepository>();

        var health = new Mock<IDownloadHealthRepository>();
        health.Setup(repository => repository.ListDownloadHealthIgnoreEntriesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ignoreEntry is null
                ? Array.Empty<DownloadHealthIgnoreEntry>()
                : [ignoreEntry]);

        var connectivity = new Mock<Deluno.Integrations.DownloadClients.IDownloadHealthConnectivityCheck>();

        var planner = new WorkPlanner(
            NullLogger<WorkPlanner>.Instance,
            jobs.Object,
            new ConfigurationBuilder().Build(),
            TimeProvider.System);

        await planner.PlanSharingReclaimAsync(
            telemetry.Object,
            platformSettings.Object,
            connections.Object,
            libraries.Object,
            activity.Object,
            sharing.Object,
            Mock.Of<IMovieCatalogRepository>(),
            Mock.Of<ISeriesCatalogRepository>(),
            new SharingReclaimService(gateway),
            Mock.Of<IHardlinkInspector>(),
            health.Object,
            connectivity.Object,
            CancellationToken.None);
    }

    private static PlatformSettingsSnapshot DefaultSettings() => new(
        AppInstanceName: "Deluno",
        MovieRootPath: null,
        SeriesRootPath: null,
        DownloadsPath: null,
        IncompleteDownloadsPath: null,
        AutoStartJobs: true,
        EnableNotifications: true,
        RenameOnImport: true,
        UseHardlinks: true,
        CleanupEmptyFolders: true,
        MovieFolderFormat: "{Movie}",
        SeriesFolderFormat: "{Series}",
        MovieFileFormat: "{Movie}",
        EpisodeFileFormat: "{Episode}",
        HostBindAddress: "127.0.0.1",
        HostPort: 7979,
        UrlBase: string.Empty,
        RequireAuthentication: false,
        UiTheme: "dark",
        DefaultMovieView: "poster",
        DefaultShowView: "poster",
        MetadataArtworkEnabled: true,
        MetadataCertificationCountry: "US",
        MetadataLanguage: "en",
        MetadataProviderMode: "auto",
        MetadataBrokerUrl: string.Empty,
        MetadataBrokerConfigured: false,
        MetadataTmdbApiKeyConfigured: false,
        MetadataOmdbApiKeyConfigured: false,
        ReleaseNeverGrabPatterns: string.Empty,
        SearchScoringMode: "balanced",
        ImportRecoveryRetentionDays: 30,
        UpdatedUtc: DateTimeOffset.UnixEpoch);
}
