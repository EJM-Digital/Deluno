using Deluno.Tray;
using Deluno.Worker.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// #1080 in tray mode: a critical background service that gives up must really stop the
/// web host the tray started, the tray must hear about it, and a fresh host must start
/// in the same process. Real Kestrel, real failure monitor, real supervisor; nothing on
/// the stop path is faked.
/// </summary>
public sealed class UnexpectedStopGuardTests
{
    // The monitor picks critical services by type name.
    private sealed class DelunoHeartbeatWorker(bool fail) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (fail)
            {
                await Task.Delay(400, CancellationToken.None); // reason: a worker that fails a moment after it starts, as a real one does; the guard under test must still catch it
                throw new InvalidOperationException("forced failure");
            }

            await Task.Delay(Timeout.Infinite, stoppingToken); // reason: a worker that runs until stopped
        }
    }

    private sealed class DelegateHost(Func<CancellationToken, Task> start) : IStartableHost
    {
        public Task StartAsync(CancellationToken cancellationToken) => start(cancellationToken);
        public Task StopAsync() => Task.CompletedTask;
        public Task FreeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class SilentLog : ISupervisorLog
    {
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message) { }
    }

    private sealed class TestHost
    {
        public required WebApplication App { get; init; }
        public required string Url { get; init; }
    }

    private static async Task<TestHost> StartHostAsync(bool failing)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration["urls"] = "http://127.0.0.1:0";
        builder.Services.AddSingleton(new BackgroundServiceSafetyOptions
        {
            // Exactly what DelunoServer registers in tray mode: no ExitProcess.
            SetExitCodeWhenGivingUp = false,
            MaxRestarts = 1,
            RestartBackoff = TimeSpan.FromMilliseconds(10)
        });
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<BackgroundServiceFailureMonitor>();
        builder.Services.AddSingleton<IHostedService>(p => p.GetRequiredService<BackgroundServiceFailureMonitor>());
        builder.Services.AddSingleton<IHostedService>(new DelunoHeartbeatWorker(failing));
        var app = builder.Build();
        app.MapGet("/api/health/ready", () => "ready");
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new TestHost { App = app, Url = url };
    }

    private static async Task<bool> AnswersAsync(string url)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            using var response = await client.GetAsync(url + "/api/health/ready");
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [Fact]
    public async Task A_critical_service_giving_up_stops_the_host_and_a_fresh_host_answers()
    {
        var expectedStop = false;
        var current = default(TestHost);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;

        async Task StartAsync(CancellationToken _)
        {
            // The first host has the failing service; the replacement is healthy.
            var host = await StartHostAsync(failing: Interlocked.Increment(ref starts) == 1);
            var started = host;
            current = started;
            expectedStop = false;
            UnexpectedStopGuard.Attach(
                host.App,
                () => expectedStop || !ReferenceEquals(current, started),
                () => stopped.TrySetResult(),
                TimeSpan.FromSeconds(10));
        }

        // The supervisor's own waits are tiny here; what takes real time is Kestrel and the failure monitor, which are
        // the things this test is about. The supervisor's timing is tested on a fake clock in HostSupervisorTests.
        var supervisor = new HostSupervisor(
            new DelegateHost(StartAsync),
            new StartProgress(TimeProvider.System, null, Environment.ProcessId),
            new HostRestartReporter { Recovered = (_, _) => recovered.TrySetResult() },
            new SilentLog(),
            TimeProvider.System,
            new HostSupervisorSettings { BaseBackoff = TimeSpan.FromMilliseconds(10), MaxBackoff = TimeSpan.FromMilliseconds(20) });
        // This is DelunoServer's wiring: the unexpected stop is handed to the supervisor.
        var wired = stopped.Task.ContinueWith(_ => supervisor.StartAsync(StartReason.UnexpectedStop), TaskScheduler.Default).Unwrap();

        await StartAsync(CancellationToken.None);
        var first = current!;
        Assert.True(await AnswersAsync(first.Url), "the first host serves until the service gives up");

        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await wired;

        Assert.False(await AnswersAsync(first.Url), "the first host must no longer be serving");
        Assert.Equal(2, starts);
        Assert.NotEqual(first.Url, current!.Url);
        Assert.True(await AnswersAsync(current.Url), "the fresh host answers /api/health/ready");

        expectedStop = true;
        await current.App.StopAsync();
        await current.App.DisposeAsync();
    }

    [Fact]
    public async Task A_normal_stop_never_raises_an_unexpected_stop()
    {
        var host = await StartHostAsync(failing: false);
        var expectedStop = false;
        var raised = 0;
        UnexpectedStopGuard.Attach(host.App, () => expectedStop, () => Interlocked.Increment(ref raised), TimeSpan.FromSeconds(5));

        expectedStop = true;
        await host.App.StopAsync();
        await host.App.DisposeAsync();
        await Task.Delay(300); // reason: shows no unexpected-stop alarm follows an expected stop; absence needs real time

        Assert.Equal(0, raised);
    }
}
