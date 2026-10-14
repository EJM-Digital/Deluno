using Deluno.Worker.Companions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Worker.Tests.Companions;

/// <summary>
/// #1080: on a starved machine a request inside one pass can time out, which .NET raises as a
/// cancellation. That must be logged and tried again, not end the loop for good, or the apps the
/// installer added would never be connected.
/// </summary>
public sealed class InstalledComponentWiringServiceLoopTests
{
    [Fact]
    public async Task A_request_that_times_out_in_one_pass_does_not_end_the_loop()
    {
        var passes = 0;
        var provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(Deluno.Companions.ICompanionLoginCleanup)))
            .Returns(() =>
            {
                Interlocked.Increment(ref passes);
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 8 seconds elapsing.");
            });
        var scope = new Mock<IServiceScope>();
        scope.SetupGet(s => s.ServiceProvider).Returns(provider.Object);
        var scopes = new Mock<IServiceScopeFactory>();
        scopes.Setup(f => f.CreateScope()).Returns(scope.Object);

        using var service = new InstalledComponentWiringService(
            scopes.Object,
            NullLogger<InstalledComponentWiringService>.Instance)
        {
            StartDelay = TimeSpan.Zero,
            TickInterval = TimeSpan.FromMilliseconds(10),
        };

        await service.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref passes) < 3 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20); // reason: poll interval inside a bounded wait (10 s above)
        }

        Assert.True(Volatile.Read(ref passes) >= 3, "the loop should have kept trying after a timed-out pass");
        Assert.False(service.ExecuteTask!.IsCompleted, "a timed-out pass must not end the loop");

        await service.StopAsync(CancellationToken.None);
        Assert.True(service.ExecuteTask.IsCompletedSuccessfully, "stopping is still a clean end");
    }
}
