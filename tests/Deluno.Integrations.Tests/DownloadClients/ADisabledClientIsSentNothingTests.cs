using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Connections.Data;
using Deluno.Infrastructure.Resilience;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Moq;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>A download client whose Enabled switch is Off is never sent a release: the grab answers that it is disabled and no client is reached.</summary>
public sealed class ADisabledClientIsSentNothingTests
{
    private static DownloadClientItem Client(bool enabled)
    {
        var now = DateTimeOffset.UnixEpoch;
        return new DownloadClientItem(
            "client-1", "Test Client", "qbittorrent", "localhost", 8080, null, null, null, false, null,
            "movies", "tv", null, 1, enabled, "healthy", null, null, null, now, now, now);
    }

    private static async Task<(DownloadClientGrabResult Result, Mock<IDownloadClientRegistry> Registry, Mock<IDownloadDispatchesRepository> Dispatches)> GrabAsync(bool enabled)
    {
        var connections = new Mock<IConnectionsRepository>();
        connections.Setup(repository => repository.ListDownloadClientsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Client(enabled)]);
        connections.Setup(repository => repository.ListDownloadClientPathMappingsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var registry = new Mock<IDownloadClientRegistry>();
        var dispatches = new Mock<IDownloadDispatchesRepository>();
        var service = new DownloadClientGrabService(
            connections.Object,
            registry.Object,
            Mock.Of<IIntegrationResiliencePolicy>(),
            dispatches.Object,
            Mock.Of<IRealtimeEventPublisher>());

        var result = await service.GrabAsync("client-1", new DownloadClientGrabRequest(
            ReleaseName: "Example.Release.WEB.1080p",
            DownloadUrl: "https://example.invalid/release/1.torrent",
            MediaType: "movies",
            Category: null, IndexerName: null), CancellationToken.None);
        return (result, registry, dispatches);
    }

    [Fact]
    public async Task A_disabled_client_answers_paused_and_is_never_reached_or_recorded()
    {
        var (result, registry, dispatches) = await GrabAsync(enabled: false);

        Assert.False(result.Succeeded);
        Assert.Equal(GrabStatuses.Paused, result.Status);
        Assert.Equal("Download client is disabled.", result.Message);
        registry.VerifyNoOtherCalls();
        dispatches.VerifyNoOtherCalls();
    }
}
