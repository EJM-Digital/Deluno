using Deluno.Integrations.DownloadClients;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1538: the edits to the held reading (a removed download, a dismissed finding, a release sent again) ignore a blank id, a store
/// that holds nothing ignores all of them, and the read behind a dismiss never fails the dismiss.
/// </summary>
public sealed class TheHeldReadingEditsAndTheBackgroundReReadTests
{
    private sealed class HoldsNothing : IDownloadTelemetrySnapshotStore
    {
        public DownloadTelemetryCapture? Current => null;

        public DateTimeOffset? CapturedUtc => null;

        public Task<SnapshotEntry<DownloadTelemetryCapture>?> GetLatestAsync(TimeSpan maxAge, CancellationToken cancellationToken)
            => Task.FromResult<SnapshotEntry<DownloadTelemetryCapture>?>(null);

        public Task<SnapshotEntry<DownloadTelemetryCapture>> RefreshAsync(TimeSpan reuseWithin, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public void MarkStale() { }
    }

    [Fact]
    public void A_store_that_holds_nothing_has_nothing_to_forget_release_or_drop()
    {
        IDownloadTelemetrySnapshotStore store = new HoldsNothing();

        store.Forget("client-1", "a");
        store.Release("client-1", "a");
        store.DropFindings("client-1", "a", "no-progress");

        Assert.Null(store.Current);
    }

    [Fact]
    public void A_blank_id_forgets_releases_and_drops_nothing_and_does_not_throw()
    {
        var service = new Mock<IDownloadClientTelemetryService>();
        var provider = new ServiceCollection().AddSingleton(service.Object).BuildServiceProvider();
        var store = new DownloadTelemetrySnapshotStore(provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, NullLogger<DownloadTelemetrySnapshotStore>.Instance);

        store.Forget("client-1", string.Empty);
        store.Release("client-1", string.Empty);
        store.DropFindings("client-1", string.Empty, null);

        Assert.Null(store.Current);
    }

    [Fact]
    public async Task A_read_that_fails_behind_a_dismiss_is_swallowed()
    {
        var store = new Mock<IDownloadTelemetrySnapshotStore>();
        store.Setup(instance => instance.RefreshAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("a client broke"));

        await DownloadClientEndpointRouteBuilderExtensions.ReadClientsAgainAsync(store.Object);

        store.Verify(instance => instance.RefreshAsync(TimeSpan.Zero, It.IsAny<CancellationToken>()), Times.Once);
    }
}
