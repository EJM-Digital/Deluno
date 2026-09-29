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

    // Named like the real critical services so the default policy treats them as critical.
    private sealed class HealthCheckHostedService : BackgroundService
    {
        public static int Starts;
        public static bool AlwaysFail;
        public static readonly TaskCompletionSource RunningSteadily = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Yield();
            var start = Interlocked.Increment(ref Starts);
            if (AlwaysFail || start == 1)
            {
                throw new InvalidOperationException("first start failed");
            }

            RunningSteadily.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static IHost BuildCriticalHost(Action<BackgroundServiceSafetyOptions> configure)
    {
        var options = new BackgroundServiceSafetyOptions
        {
            RestartBackoff = TimeSpan.FromMilliseconds(10),
            SetExitCodeWhenGivingUp = false
        };
        configure(options);
        return Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(options);
                services.AddDelunoBackgroundServiceSafety();
                services.AddHostedService<HealthCheckHostedService>();
            })
            .Build();
    }

    [Fact]
    public async Task A_critical_service_that_dies_is_restarted_in_process_and_the_host_stays_up()
    {
        HealthCheckHostedService.Starts = 0;
        HealthCheckHostedService.AlwaysFail = false;
        using var host = BuildCriticalHost(_ => { });

        await host.StartAsync();
        try
        {
            await HealthCheckHostedService.RunningSteadily.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, HealthCheckHostedService.Starts);
            Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
            Assert.Empty(host.Services.GetRequiredService<IBackgroundServiceFailureState>().Failures);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task A_critical_service_that_cannot_be_restarted_stops_the_host_so_it_is_started_again_from_outside()
    {
        HealthCheckHostedService.Starts = 0;
        HealthCheckHostedService.AlwaysFail = true;
        try
        {
            using var host = BuildCriticalHost(options => options.MaxRestarts = 2);
            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            await host.StartAsync();
            try
            {
                var stopping = new TaskCompletionSource();
                lifetime.ApplicationStopping.Register(() => stopping.TrySetResult());
                await stopping.Task.WaitAsync(TimeSpan.FromSeconds(5));

                // The first run plus two restarts, then it gave up.
                Assert.Equal(3, HealthCheckHostedService.Starts);
                var failure = Assert.Single(host.Services.GetRequiredService<IBackgroundServiceFailureState>().Failures);
                Assert.Equal("first start failed", failure.Reason);
            }
            finally
            {
                await host.StopAsync();
            }
        }
        finally
        {
            HealthCheckHostedService.AlwaysFail = false;
        }
    }

    [Fact]
    public async Task A_reported_problem_shows_on_Health_and_clears_when_it_is_cleared()
    {
        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services => services.AddDelunoBackgroundServiceSafety())
            .Build();
        await host.StartAsync();
        try
        {
            var reporter = host.Services.GetRequiredService<IBackgroundProblemReporter>();
            var state = host.Services.GetRequiredService<IBackgroundServiceFailureState>();

            reporter.Report("lane:import", "Background jobs: import lane keeps failing", "Failed 3 times.", "database is locked");
            var outcome = Assert.Single(await new BackgroundServiceHealthCheck(state).RunAsync(CancellationToken.None));
            Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
            Assert.Contains("import lane", outcome.Name);
            Assert.Equal("database is locked", outcome.Cause);

            reporter.Clear("lane:import");
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
