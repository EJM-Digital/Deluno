using System.Runtime.CompilerServices;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search.Trackers;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Worker.HealthChecks.Checks;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>
/// "Never say the same thing twice" (#1196): when two items have the same
/// problem, each row names its own item in its message, cause and fix, so a
/// screen listing them never repeats one sentence. The other per-item checks
/// (indexers, remote path mappings, tags, processors, hand-offs, downloads and
/// stray processor output) assert the same in their own test files.
/// </summary>
public sealed class SameProblemReadsDifferentlyPerItemTests
{
    internal static void AssertEachReadsDifferently(IReadOnlyList<HealthCheckOutcome> outcomes, int expected = 2)
    {
        var problems = outcomes.Where(outcome => outcome.Status == HealthCheckStatuses.Problem).ToArray();
        Assert.Equal(expected, problems.Length);
        Assert.Equal(expected, problems.Select(outcome => outcome.Message).Distinct().Count());
        Assert.Equal(expected, problems.Select(outcome => outcome.Cause).Distinct().Count());
        Assert.Equal(expected, problems.Select(outcome => outcome.Fix).Distinct().Count());
    }

    [Fact]
    public async Task Two_media_servers_with_the_same_failure_read_differently()
    {
        static MediaServerConnection Server(string name) =>
            ((MediaServerConnection)RuntimeHelpers.GetUninitializedObject(typeof(MediaServerConnection))) with
            {
                Id = name, Name = name, IsEnabled = true, HealthStatus = "unreachable", LastHealthMessage = "Connection refused."
            };

        var servers = new Mock<IMediaServerRepository>();
        servers.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Server("Plex Den"), Server("Emby Study")]);
        var subtitles = new Mock<ISubtitleProviderRepository>();
        subtitles.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var seerr = new Mock<ISeerrRepository>();
        seerr.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        AssertEachReadsDifferently(await new ExternalServiceHealthCheck(servers.Object, subtitles.Object, seerr.Object).RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Two_unreachable_proxies_read_differently()
    {
        static ProxyItem Proxy(string name) => new(
            Guid.NewGuid().ToString("N"), name, ProxyTypes.Socks5, "socks5://192.168.1.1:1080", null, null, true,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch) { AssignedIndexerNames = [] };

        var connections = new Mock<IConnectionsRepository>();
        connections.Setup(repo => repo.ListProxiesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Proxy("Home SOCKS5"), Proxy("Office SOCKS5")]);
        var tester = new Mock<IProxyReachabilityTester>();
        tester.Setup(instance => instance.TestAsync(It.IsAny<ProxyItem>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProxyReachabilityResult(false, "Connection refused."));

        AssertEachReadsDifferently(await new ProxyHealthCheck(connections.Object, tester.Object).RunAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(LibraryRootProblem.Unreachable)]
    [InlineData(LibraryRootProblem.ReadOnly)]
    [InlineData(LibraryRootProblem.LowFreeSpace)]
    [InlineData(LibraryRootProblem.None)]
    public async Task Two_library_roots_with_the_same_problem_read_differently(LibraryRootProblem problem)
    {
        static LibraryItem Library(string id, string name) =>
            ((LibraryItem)RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem))) with { Id = id, Name = name, RootPath = "/media/" + id };

        var libraries = new[] { Library("l1", "Films"), Library("l2", "Shows") };
        var statuses = libraries.ToDictionary(
            library => library.Id,
            library => new LibraryRootStatus(
                library.Id, library.Name, library.RootPath, false, false, 1, problem,
                WarnLowFreeSpace: problem == LibraryRootProblem.None,
                $"The {library.Name} library's root at {library.RootPath} has a problem."));
        var repository = new Mock<ILibrariesRepository>();
        repository.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(libraries);
        var availability = new Mock<ILibraryAvailabilityService>();
        availability.Setup(service => service.ReadAsync(It.IsAny<IReadOnlyList<LibraryItem>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LibraryAvailability(new HashSet<string>(), statuses));

        AssertEachReadsDifferently(await new LibraryRootHealthCheck(repository.Object, availability.Object).RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Two_download_clients_low_on_space_read_differently()
    {
        static DownloadClientTelemetrySnapshot Snapshot(string name) =>
            ((DownloadClientTelemetrySnapshot)RuntimeHelpers.GetUninitializedObject(typeof(DownloadClientTelemetrySnapshot))) with
            {
                ClientId = name, ClientName = name, Protocol = "qbittorrent", FreeSpaceBytes = 1_000_000_000,
                LastHealthMessage = "Only 1 GB free — downloads will start failing."
            };

        var overview = ((DownloadTelemetryOverview)RuntimeHelpers.GetUninitializedObject(typeof(DownloadTelemetryOverview))) with
        {
            Clients = [Snapshot("qBit One"), Snapshot("qBit Two")]
        };
        var telemetry = new Mock<IDownloadClientTelemetryService>();
        telemetry.Setup(service => service.GetLatestOverviewAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(overview);

        AssertEachReadsDifferently(await new ClientDownloadFolderFreeSpaceHealthCheck(telemetry.Object).RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Two_tracker_indexers_with_a_missing_site_read_differently()
    {
        static IndexerItem Indexer(string name, string site) => new(
            name, name, IndexerProtocols.Tracker, "private", "http://localhost", null, 1, "2000", "", "both", true,
            "healthy", null, null, null, null, 0, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)
        { DefinitionId = site };

        var connections = new Mock<IConnectionsRepository>();
        connections.Setup(repo => repo.ListIndexersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([Indexer("Tracker One", "gone-one"), Indexer("Tracker Two", "gone-two")]);
        var catalog = new Mock<ITrackerDefinitionCatalog>();

        AssertEachReadsDifferently(await new TrackerSiteHealthCheck(connections.Object, catalog.Object).RunAsync(CancellationToken.None));
    }
}
