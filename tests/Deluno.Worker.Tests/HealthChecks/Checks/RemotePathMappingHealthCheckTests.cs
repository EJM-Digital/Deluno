using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Worker.HealthChecks.Checks;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>
/// A remote path mapping's local side, and a download client's reported save
/// path translated through it, checked on the same schedule as every other
/// health check (#868) rather than only when a grab happens to run through
/// it.
/// </summary>
public sealed class RemotePathMappingHealthCheckTests
{
    [Fact]
    public async Task A_mapping_whose_local_path_exists_and_is_readable_passes()
    {
        var localPath = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var mapping = Mapping(localPath: localPath);
            var check = new RemotePathMappingHealthCheck(
                ConnectionsReturning(mappings: [mapping]),
                RegistryReturning(null));

            var outcomes = await check.RunAsync(CancellationToken.None);

            var outcome = Assert.Single(outcomes);
            Assert.Equal(HealthCheckStatuses.Ok, outcome.Status);
            Assert.Equal(HealthCheckCategories.RemotePathMapping, outcome.Category);
        }
        finally
        {
            Directory.Delete(localPath, recursive: true);
        }
    }

    [Fact]
    public async Task A_mapping_whose_local_path_is_missing_is_flagged()
    {
        var localPath = Path.Combine(Path.GetTempPath(), "deluno-missing-" + Guid.NewGuid().ToString("n"));
        var mapping = Mapping(localPath: localPath);
        var check = new RemotePathMappingHealthCheck(
            ConnectionsReturning(mappings: [mapping]),
            RegistryReturning(null));

        var outcomes = await check.RunAsync(CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Contains("does not exist", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task No_mappings_and_no_clients_reports_not_in_use()
    {
        var check = new RemotePathMappingHealthCheck(
            ConnectionsReturning(mappings: []),
            RegistryReturning(null));

        var outcomes = await check.RunAsync(CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(HealthCheckStatuses.Ok, outcome.Status);
        Assert.Contains("not in use", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_clients_reported_save_path_that_translates_to_a_real_folder_passes()
    {
        var localPath = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var client = Client();
            var mapping = Mapping(remotePath: "/downloads/movies", localPath: localPath, clientId: client.Id);
            var check = new RemotePathMappingHealthCheck(
                ConnectionsReturning(clients: [client], mappings: [mapping]),
                RegistryReturning("/downloads/movies"));

            var outcomes = await check.RunAsync(CancellationToken.None);

            var savePathOutcome = Assert.Single(outcomes, outcome => outcome.Id.StartsWith("remote-path-mapping:save-path:", StringComparison.Ordinal));
            Assert.Equal(HealthCheckStatuses.Ok, savePathOutcome.Status);
        }
        finally
        {
            Directory.Delete(localPath, recursive: true);
        }
    }

    [Fact]
    public async Task A_clients_reported_save_path_that_translates_to_a_missing_folder_is_flagged()
    {
        var client = Client();
        var localPath = Path.Combine(Path.GetTempPath(), "deluno-missing-" + Guid.NewGuid().ToString("n"));
        var mapping = Mapping(remotePath: "/downloads/movies", localPath: localPath, clientId: client.Id);
        var check = new RemotePathMappingHealthCheck(
            ConnectionsReturning(clients: [client], mappings: [mapping]),
            RegistryReturning("/downloads/movies"));

        var outcomes = await check.RunAsync(CancellationToken.None);

        var savePathOutcome = Assert.Single(outcomes, outcome => outcome.Id.StartsWith("remote-path-mapping:save-path:", StringComparison.Ordinal));
        Assert.Equal(HealthCheckStatuses.Problem, savePathOutcome.Status);
    }

    [Fact]
    public async Task A_client_Deluno_names_the_folder_for_on_every_grab_has_no_category_folder_to_check()
    {
        // #1060: Transmission and Deluge are sent the library downloads folder per grab (#1073), so the
        // "<download folder>/<category>" they report on a clean install is never used and never missing.
        var missing = Path.Combine(Path.GetTempPath(), "deluno-missing-" + Guid.NewGuid().ToString("n"), "deluno-movies");
        var check = new RemotePathMappingHealthCheck(
            ConnectionsReturning(clients: [Client()], mappings: []),
            RegistryReturning(missing, perGrab: true));

        var outcomes = await check.RunAsync(CancellationToken.None);

        Assert.DoesNotContain(outcomes, outcome => outcome.Status == HealthCheckStatuses.Problem);
        Assert.DoesNotContain(outcomes, outcome => outcome.Id.StartsWith("remote-path-mapping:save-path:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_client_with_its_own_category_folder_still_has_a_missing_one_flagged()
    {
        var missing = Path.Combine(Path.GetTempPath(), "deluno-missing-" + Guid.NewGuid().ToString("n"), "Movies");
        var check = new RemotePathMappingHealthCheck(
            ConnectionsReturning(clients: [Client()], mappings: []),
            RegistryReturning(missing, perGrab: false));

        var outcomes = await check.RunAsync(CancellationToken.None);

        Assert.Contains(outcomes, outcome => outcome.Status == HealthCheckStatuses.Problem && outcome.Message.Contains("does not exist", StringComparison.Ordinal));
    }

    private static DownloadClientPathMappingItem Mapping(string localPath, string remotePath = "/downloads/complete", string clientId = "client-1")
    {
        var now = DateTimeOffset.UnixEpoch;
        return new DownloadClientPathMappingItem("mapping-1", clientId, remotePath, localPath, true, 1, now, now);
    }

    private static DownloadClientItem Client()
    {
        var now = DateTimeOffset.UnixEpoch;
        return new DownloadClientItem(
            Id: "client-1",
            Name: "Test Client",
            Protocol: "qbittorrent",
            Host: "localhost",
            Port: 8080,
            Username: null,
            Secret: null,
            EndpointUrl: null,
            UseSsl: false,
            UrlBase: null,
            MoviesCategory: "movies",
            TvCategory: null,
            CategoryTemplate: null,
            Priority: 1,
            IsEnabled: true,
            HealthStatus: "healthy",
            LastHealthMessage: null,
            LastHealthFailureCategory: null,
            LastHealthLatencyMs: null,
            LastHealthTestUtc: now,
            CreatedUtc: now,
            UpdatedUtc: now);
    }

    private static IConnectionsRepository ConnectionsReturning(
        IReadOnlyList<DownloadClientPathMappingItem> mappings,
        IReadOnlyList<DownloadClientItem>? clients = null)
    {
        var repository = new Mock<IConnectionsRepository>();
        repository.Setup(instance => instance.ListDownloadClientsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(clients ?? []);
        repository.Setup(instance => instance.ListDownloadClientPathMappingsAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mappings);
        return repository.Object;
    }

    private static IDownloadClientRegistry RegistryReturning(string? reportedSavePath, bool perGrab = false)
    {
        var registry = new Mock<IDownloadClientRegistry>();
        if (reportedSavePath is null)
        {
            IDownloadClient? none = null;
            registry.Setup(instance => instance.TryGet(It.IsAny<string?>(), out none)).Returns(false);
            return registry.Object;
        }

        var client = new Mock<IDownloadClient>();
        client.Setup(instance => instance.CheckCategoryAsync(It.IsAny<DownloadClientItem>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DownloadClientCategoryCheckResult("client-1", "Test Client", "movies", DownloadClientCategoryStatuses.Ready, "Ready.", true, true, reportedSavePath));

        client.SetupGet(instance => instance.SetsSaveFolderPerGrab).Returns(perGrab);
        var captured = client.Object;
        registry.Setup(instance => instance.TryGet(It.IsAny<string?>(), out captured)).Returns(true);
        return registry.Object;
    }

    [Fact]
    public async Task Two_mappings_with_missing_local_folders_read_differently()
    {
        var first = Mapping(localPath: Path.Combine(Path.GetTempPath(), "deluno-missing-a-" + Guid.NewGuid().ToString("n")), remotePath: "/downloads/movies");
        var second = Mapping(localPath: Path.Combine(Path.GetTempPath(), "deluno-missing-b-" + Guid.NewGuid().ToString("n")), remotePath: "/downloads/tv");
        var check = new RemotePathMappingHealthCheck(ConnectionsReturning(mappings: [first, second]), RegistryReturning(null));

        SameProblemReadsDifferentlyPerItemTests.AssertEachReadsDifferently(await check.RunAsync(CancellationToken.None));
    }
}
