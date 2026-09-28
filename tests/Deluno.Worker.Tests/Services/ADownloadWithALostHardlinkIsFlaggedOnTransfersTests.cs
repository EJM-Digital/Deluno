using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
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
/// #981: a seeding download whose hardlink into the library is gone is a
/// different fact from a missing library file - the download is still on
/// disk and safe to reclaim. <see cref="WorkPlanner.PlanSharingReclaimAsync"/>
/// already computes this in its sharing pass (WorkPlanner.cs, around the
/// unlinkedDetail/LostHardlink handling); this covers that it actually
/// reaches <see cref="IDownloadSharingRepository.ReplaceHoldsAsync"/> with
/// <see cref="DownloadSharingHold.LostHardlink"/> set, once the real files
/// disagree with the heuristic.
/// </summary>
public sealed class ADownloadWithALostHardlinkIsFlaggedOnTransfersTests
{
    private const string DownloadPath = @"C:\downloads\Arrival.2016.WEB.1080p-GROUP\Arrival.2016.WEB.1080p-GROUP.mkv";
    private const string LibraryPath = @"C:\library\Movies\Arrival (2016)\Arrival (2016).mkv";

    private sealed class NoOpGateway : IDownloadClientActionGateway
    {
        public Task<DownloadClientRemovalResult> RemoveWithDataAsync(string clientId, string queueItemId, CancellationToken cancellationToken)
            => Task.FromResult(new DownloadClientRemovalResult(true, "Removed."));

        public Task<DownloadClientRemovalResult> StopSeedingAsync(string clientId, string queueItemId, CancellationToken cancellationToken)
            => Task.FromResult(new DownloadClientRemovalResult(true, "Stopped."));
    }

    [Fact]
    public async Task The_sharing_pass_flags_a_lost_hardlink_as_a_distinct_hold()
    {
        var holds = await RunSharingPassAsync(stillLinked: false);

        var hold = Assert.Single(holds);
        Assert.True(hold.LostHardlink);
        Assert.False(hold.SharesLibraryCopy);
        Assert.Equal("No longer shared with your library - the library copy was upgraded or removed.", hold.Detail);
    }

    /// <summary>
    /// The control: the same download, with the real file still linked to the
    /// library, is never flagged. Without this, a pass that (by defect)
    /// always set <see cref="DownloadSharingHold.LostHardlink"/> would pass
    /// the test above too.
    /// </summary>
    [Fact]
    public async Task The_same_download_is_not_flagged_when_the_hardlink_is_intact()
    {
        var holds = await RunSharingPassAsync(stillLinked: true);

        var hold = Assert.Single(holds);
        Assert.False(hold.LostHardlink);
        Assert.True(hold.SharesLibraryCopy);
    }

    private static async Task<IReadOnlyList<DownloadSharingHold>> RunSharingPassAsync(bool stillLinked)
    {
        var jobs = new Mock<IJobQueueRepository>();
        jobs.Setup(repository => repository.TryClaimScheduledPassAsync(
                It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        jobs.Setup(repository => repository.FindDispatchLinkByClientItemAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchCatalogueLink("dispatch-1", "movie", "movie-1", IndexerName: "example.tracker", LibraryId: "lib-1"));

        var queueItem = new DownloadQueueItem(
            Id: "item-1",
            ClientId: "client-1",
            ClientName: "qBittorrent",
            Protocol: DownloadProtocols.Torrent,
            MediaType: "movie",
            Title: "Arrival",
            ReleaseName: "Arrival.2016.WEB.1080p-GROUP",
            Category: "movies",
            Status: DownloadQueueStatuses.Imported,
            Progress: 100,
            SpeedMbps: 0,
            EtaSeconds: 0,
            SizeBytes: 4_000_000_000,
            DownloadedBytes: 4_000_000_000,
            Peers: 2,
            IndexerName: "example.tracker",
            ErrorMessage: null,
            AddedUtc: DateTimeOffset.UnixEpoch,
            SourcePath: DownloadPath,
            LibraryId: "lib-1",
            Ratio: 0.1,
            SeedingMinutes: 10);

        var snapshot = new DownloadClientTelemetrySnapshot(
            ClientId: "client-1",
            ClientName: "qBittorrent",
            Protocol: DownloadProtocols.Torrent,
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
            .ReturnsAsync([
                (LibraryItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem)) with
                {
                    Id = "lib-1",
                    Name = "Movies",
                    MediaType = "movies",
                    RootPath = @"C:\library\Movies"
                }
            ]);

        var movies = new Mock<IMovieCatalogRepository>();
        movies.Setup(repository => repository.StreamTrackedFilesAsync("lib-1", It.IsAny<CancellationToken>()))
            .Returns(() => Items(new MovieTrackedFileItem("movie-1", "lib-1", "Arrival", 2016, LibraryPath, 4_000_000_000, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)));

        var activity = new Mock<IActivityFeedRepository>();

        DownloadSharingHold[]? capturedHolds = null;
        var sharing = new Mock<IDownloadSharingRepository>();
        sharing.Setup(repository => repository.ReplaceHoldsAsync(
                It.IsAny<IReadOnlyList<DownloadSharingHold>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<DownloadSharingHold>, string?, CancellationToken>((holds, _, _) => capturedHolds = holds.ToArray())
            .Returns(Task.CompletedTask);

        var health = new Mock<IDownloadHealthRepository>();
        health.Setup(repository => repository.ListDownloadHealthIgnoreEntriesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var connectivity = new Mock<IDownloadHealthConnectivityCheck>();

        var hardlinkInspector = new Mock<IHardlinkInspector>();
        hardlinkInspector.Setup(inspector => inspector.StillLinked(DownloadPath, LibraryPath))
            .Returns(stillLinked);

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
            movies.Object,
            Mock.Of<Deluno.Series.Data.ISeriesCatalogRepository>(),
            new SharingReclaimService(new NoOpGateway()),
            hardlinkInspector.Object,
            health.Object,
            connectivity.Object,
            CancellationToken.None);

        Assert.NotNull(capturedHolds);
        return capturedHolds!;
    }

    private static async IAsyncEnumerable<T> Items<T>(params T[] items)
    {
        foreach (var item in items)
        {
            yield return item;
        }

        await Task.CompletedTask;
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
        MetadataNfoEnabled: true,
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
