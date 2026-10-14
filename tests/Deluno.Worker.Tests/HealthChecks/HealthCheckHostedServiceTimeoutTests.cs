using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Worker.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks;

/// <summary>#1080: an HttpClient timeout surfaces as a TaskCanceledException; it must not end the health check service.</summary>
public sealed class HealthCheckHostedServiceTimeoutTests
{
    /// <summary>Real timers, run a thousand times faster, so the 20 s start-up wait and 15 s tick pass in milliseconds.</summary>
    private sealed class FastTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => System.CreateTimer(callback, state, Scale(dueTime), Scale(period));

        private static TimeSpan Scale(TimeSpan value)
            => value == Timeout.InfiniteTimeSpan ? value : TimeSpan.FromTicks(value.Ticks / 1000);
    }

    [Fact]
    public async Task A_timeout_that_is_not_a_shutdown_is_logged_and_the_service_keeps_running()
    {
        var calls = 0;
        var repository = new Mock<IHealthCheckRepository>();
        repository
            .Setup(item => item.TakePendingImmediateRunsAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                calls++;
                return calls == 1
                    ? throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.")
                    : Task.FromResult<IReadOnlyList<string>>([]);
            });
        var jobs = new Mock<IJobQueueRepository>();
        jobs.Setup(item => item.TryClaimScheduledPassAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var service = new HealthCheckHostedService(
            NullLogger<HealthCheckHostedService>.Instance,
            new Mock<IServiceScopeFactory>().Object,
            repository.Object,
            jobs.Object,
            new FastTimeProvider());

        await service.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Volatile.Read(ref calls) < 3)
            {
                Assert.True(DateTime.UtcNow < deadline, "The service stopped looping after the timeout.");
                await Task.Delay(10); // reason: poll interval inside a bounded wait (10 s above)
            }

            Assert.False(service.ExecuteTask!.IsCompleted);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}
