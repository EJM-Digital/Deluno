using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Libraries.Data;
using Deluno.Worker.HealthChecks.Checks;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>The last two untested branches: a processor never tested, judged against the real clock, and a download client with no host at all.</summary>
public sealed class LastBranchesTests
{
    [Fact]
    public async Task A_processor_that_was_added_long_ago_and_never_tested_is_reported_on_the_real_clock()
    {
        var created = DateTimeOffset.UtcNow.AddDays(-1);
        var connection = new ProcessorConnectionItem("connection-1", "Weir", "generic-webhook", "https://weir.example.test/webhook", "Authorization", null, true, "unknown", "Not tested yet.", null, created, created);
        var processors = new Mock<IProcessorRepository>();
        processors.Setup(p => p.ListProcessorConnectionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([connection]);
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(l => l.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var outcome = Assert.Single(await new ProcessorHealthCheck(processors.Object, new Mock<IProcessorConnectionService>().Object, libraries.Object).RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Equal("Weir: Not tested yet.", outcome.Message);
    }

    [Fact]
    public async Task A_download_client_with_no_host_at_all_takes_only_the_mappings_made_for_it()
    {
        var now = DateTimeOffset.UnixEpoch;
        var client = new DownloadClientItem("client-1", "Test Client", "qbittorrent", null, 8080, null, null, null, false, null, "movies", null, null, 1, true, "healthy", null, null, null, now, now, now);
        var mapping = new DownloadClientPathMappingItem("m1", "client-1", "/downloads", "/mnt/dl", true, 1, now, now);
        var connections = new Mock<IConnectionsRepository>();
        connections.Setup(c => c.ListDownloadClientsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([client]);
        connections.Setup(c => c.ListDownloadClientPathMappingsAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync([mapping]);
        var implementation = new Mock<IDownloadClient>();
        implementation.Setup(c => c.CheckCategoryAsync(It.IsAny<DownloadClientItem>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DownloadClientCategoryCheckResult("client-1", "Test Client", "movies", DownloadClientCategoryStatuses.Ready, "ok", true, true, "/downloads"));
        var registry = new Mock<IDownloadClientRegistry>();
        var captured = implementation.Object;
        registry.Setup(r => r.TryGet(It.IsAny<string?>(), out captured)).Returns(true);

        var outcomes = await new RemotePathMappingHealthCheck(connections.Object, registry.Object, path => path == "/mnt/dl", _ => []).RunAsync(CancellationToken.None);

        Assert.Contains(outcomes, outcome => outcome.Message == "Test Client saves movies to /mnt/dl, and Deluno can read it.");
    }
}
