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
}
