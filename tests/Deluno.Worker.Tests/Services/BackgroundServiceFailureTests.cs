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
                await Task.Delay(Timeout.Infinite, stoppingToken); // reason: a service that runs until stopped
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
                await Task.Delay(Timeout.Infinite, stoppingToken); // reason: a service that runs until stopped
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
    public async Task Giving_up_ends_the_process_with_a_failing_exit_instead_of_a_clean_host_stop_when_asked_to()
    {
        HealthCheckHostedService.Starts = 0;
        HealthCheckHostedService.AlwaysFail = true;
        try
        {
            var exitCode = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stoppedBeforeExit = false;
            IHostApplicationLifetime? lifetime = null;
            using var host = BuildCriticalHost(options =>
            {
                options.MaxRestarts = 1;
                options.ExitDelay = TimeSpan.Zero;
                options.StopWait = TimeSpan.FromSeconds(5);
                options.ExitGrace = TimeSpan.Zero;
                options.ExitProcess = code =>
                {
                    stoppedBeforeExit = lifetime!.ApplicationStopped.IsCancellationRequested;
                    exitCode.TrySetResult(code);
                };
            });
            lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            await host.StartAsync();
            // What Host.Run does when the host is asked to stop: finish stopping the hosted services.
            lifetime.ApplicationStopping.Register(() => _ = Task.Run(() => host.StopAsync()));
            Assert.Equal(1, await exitCode.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            // A graceful stop first, so hosted services ran their StopAsync, and only then the failing exit.
            Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
            Assert.True(stoppedBeforeExit);
        }
        finally
        {
            HealthCheckHostedService.AlwaysFail = false;
        }
    }

    [Fact]
    public async Task Giving_up_sets_the_failing_service_exit_code_before_the_host_stops_and_a_normal_stop_never_does()
    {
        HealthCheckHostedService.Starts = 0;
        HealthCheckHostedService.AlwaysFail = true;
        try
        {
            var codes = new List<int>();
            var stoppingWhenSet = new List<bool>();
            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            IHostApplicationLifetime? lifetime = null;
            using var host = BuildCriticalHost(options =>
            {
                options.MaxRestarts = 1;
                options.ExitDelay = TimeSpan.Zero;
                options.ExitGrace = TimeSpan.Zero;
                options.StopWait = TimeSpan.FromSeconds(5);
                options.SetServiceExitCode = code =>
                {
                    codes.Add(code);
                    stoppingWhenSet.Add(lifetime!.ApplicationStopping.IsCancellationRequested);
                };
                options.ExitProcess = code => exited.TrySetResult(code);
            });
            lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            await host.StartAsync();
            lifetime.ApplicationStopping.Register(() => _ = Task.Run(() => host.StopAsync()));
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal([BackgroundServiceSafetyOptions.ServiceFailureExitCode], codes);
            Assert.Equal(1067, codes[0]);
            Assert.False(stoppingWhenSet[0]); // set before the stop began
        }
        finally
        {
            HealthCheckHostedService.AlwaysFail = false;
        }

        // A normal stop (no give-up) never touches the exit code.
        HealthCheckHostedService.Starts = 0;
        var normalCodes = new List<int>();
        using var normalHost = BuildCriticalHost(options => options.SetServiceExitCode = normalCodes.Add);
        await normalHost.StartAsync();
        await HealthCheckHostedService.RunningSteadily.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await normalHost.StopAsync();
        Assert.Empty(normalCodes);
    }

    [Fact]
    public async Task A_stop_that_arrives_while_giving_up_is_a_normal_stop_and_never_ends_the_process_as_a_crash()
    {
        HealthCheckHostedService.Starts = 0;
        HealthCheckHostedService.AlwaysFail = true;
        try
        {
            var exited = false;
            using var host = BuildCriticalHost(options =>
            {
                options.MaxRestarts = 1;
                options.ExitDelay = TimeSpan.FromSeconds(1);
                options.ExitGrace = TimeSpan.Zero;
                options.SetServiceExitCode = _ => exited = true;
                options.ExitProcess = _ => exited = true;
            });
            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            await host.StartAsync();

            // A person stops the service (or an update does) before the exit delay ends.
            lifetime.StopApplication();
            await Task.Delay(TimeSpan.FromSeconds(2)); // reason: shows the process does not exit after a stop; absence needs real time past the exit delay
            await host.StopAsync();

            Assert.False(exited);
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
            await Task.Delay(20); // reason: poll interval inside a bounded wait (5 s above)
        }
    }
}
