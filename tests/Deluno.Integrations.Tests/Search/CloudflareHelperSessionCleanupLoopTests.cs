using System.Runtime.CompilerServices;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Integrations.Search.Trackers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Integrations.Tests.Search;

/// <summary>
/// #1080: a helper request that times out (a cancellation to .NET) while reconciling sessions must
/// be logged and tried again, not end the reconcile loop for good.
/// </summary>
public sealed class CloudflareHelperSessionCleanupLoopTests
{
    [Fact]
    public async Task A_request_that_times_out_in_one_pass_does_not_end_the_loop()
    {
        var passes = 0;
        var connections = new Mock<IConnectionsRepository>();
        connections.Setup(c => c.ListIndexersAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref passes);
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 8 seconds elapsing.");
            });

        using var service = new CloudflareHelperSessionCleanupService(
            Mock.Of<ICloudflareHelper>(),
            connections.Object,
            NullLogger<CloudflareHelperSessionCleanupService>.Instance)
        {
            ReconcileInterval = TimeSpan.FromMilliseconds(10),
        };

        await service.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref passes) < 3 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20); // reason: poll interval inside a bounded wait (10 s above)
        }

        Assert.True(Volatile.Read(ref passes) >= 3, "the loop should have kept reconciling after a timed-out pass");
        Assert.False(service.ExecuteTask!.IsCompleted, "a timed-out pass must not end the loop");

        await service.StopAsync(CancellationToken.None);
    }

    private static IndexerItem Indexer(string baseUrl, bool enabled)
        => (IndexerItem)RuntimeHelpers.GetUninitializedObject(typeof(IndexerItem)) with { BaseUrl = baseUrl, IsEnabled = enabled };

    [Fact]
    public async Task On_its_interval_only_the_hosts_of_enabled_indexers_are_kept_and_the_rest_of_the_sessions_are_closed()
    {
        var kept = new List<string[]>();
        var helper = new Mock<ICloudflareHelper>();
        helper.Setup(h => h.ReconcileSessionsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyCollection<string> hosts, CancellationToken _) => kept.Add(hosts.ToArray()))
            .Returns(Task.CompletedTask);
        var connections = new Mock<IConnectionsRepository>();
        connections.Setup(c => c.ListIndexersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([Indexer("https://tracker-on.example", true), Indexer("https://tracker-off.example", false)]);

        using var service = new CloudflareHelperSessionCleanupService(helper.Object, connections.Object, NullLogger<CloudflareHelperSessionCleanupService>.Instance)
        {
            ReconcileInterval = TimeSpan.FromMilliseconds(10),
        };
        await service.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (kept.Count < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20); // reason: poll interval inside a bounded wait (10 s above)
        }

        await service.StopAsync(CancellationToken.None);
        Assert.NotEmpty(kept);
        Assert.Equal(["tracker-on.example"], kept[0]);
    }

    [Fact]
    public async Task At_shutdown_every_session_is_closed_and_a_helper_that_cannot_be_reached_does_not_stop_the_shutdown()
    {
        var helper = new Mock<ICloudflareHelper>();
        helper.Setup(h => h.DestroyAllSessionsAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var connections = new Mock<IConnectionsRepository>();
        connections.Setup(c => c.ListIndexersAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        using var service = new CloudflareHelperSessionCleanupService(helper.Object, connections.Object, NullLogger<CloudflareHelperSessionCleanupService>.Instance);
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        helper.Verify(h => h.DestroyAllSessionsAsync(It.IsAny<CancellationToken>()), Times.Once);

        var unreachable = new Mock<ICloudflareHelper>();
        unreachable.Setup(h => h.DestroyAllSessionsAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("The helper did not answer."));
        using var second = new CloudflareHelperSessionCleanupService(unreachable.Object, connections.Object, NullLogger<CloudflareHelperSessionCleanupService>.Instance);
        await second.StartAsync(CancellationToken.None);
        var stopped = await Record.ExceptionAsync(() => second.StopAsync(CancellationToken.None));
        Assert.Null(stopped);
        unreachable.Verify(h => h.DestroyAllSessionsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
