using Deluno.Contracts;
using Deluno.Worker;
using Deluno.Worker.HealthChecks.Checks;
using Deluno.Worker.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Deluno.Worker.Tests.Services;

public sealed class BackgroundServiceFailureTests
{
    private sealed class ExplodingHostedService : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Yield();
            throw new InvalidOperationException("the database was busy");
        }
    }

    private sealed class SteadyHostedService : BackgroundService
    {
        public static readonly TaskCompletionSource Ran = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Yield();
            Ran.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [Fact]
    public async Task A_background_service_that_throws_does_not_stop_the_host_and_shows_on_Health()
    {
        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddDelunoBackgroundServiceSafety();
                services.AddHostedService<ExplodingHostedService>();
                services.AddHostedService<SteadyHostedService>();
            })
            .Build();

        await host.StartAsync();
        try
        {
            var state = host.Services.GetRequiredService<IBackgroundServiceFailureState>();
            await WaitForAsync(() => state.Failures.Count > 0);
            await SteadyHostedService.Ran.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // The host is still up: the default behaviour would have asked it to stop.
            Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);

            var failure = Assert.Single(state.Failures);
            Assert.Equal(nameof(ExplodingHostedService), failure.TypeName);
            Assert.Equal("the database was busy", failure.Reason);

            var outcomes = await new BackgroundServiceHealthCheck(state).RunAsync(CancellationToken.None);
            var outcome = Assert.Single(outcomes);
            Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
            Assert.Equal(HealthCheckCategories.System, outcome.Category);
            Assert.Contains("stopped after an error", outcome.Message);
            Assert.Contains("Restart Deluno", outcome.Fix);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Health_reports_nothing_when_every_background_service_is_running()
    {
        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddDelunoBackgroundServiceSafety();
                services.AddHostedService<SteadyHostedService>();
            })
            .Build();

        await host.StartAsync();
        try
        {
            await SteadyHostedService.Ran.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var state = host.Services.GetRequiredService<IBackgroundServiceFailureState>();
            Assert.Empty(await new BackgroundServiceHealthCheck(state).RunAsync(CancellationToken.None));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Theory]
    [InlineData("RankingModelTrainingHostedService", "Ranking model training")]
    [InlineData("DelunoHeartbeatWorker", "Deluno heartbeat worker")]
    [InlineData("TrackerSiteSwitchHostedService", "Tracker site switch")]
    public void Names_are_plain_words(string typeName, string expected)
        => Assert.Equal(expected, BackgroundServiceFailureMonitor.FriendlyName(typeName));

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the failure to be recorded.");
            await Task.Delay(20);
        }
    }
}
