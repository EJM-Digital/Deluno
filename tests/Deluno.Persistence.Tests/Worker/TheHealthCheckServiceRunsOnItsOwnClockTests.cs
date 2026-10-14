using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Notifications;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Worker.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Deluno.Persistence.Tests.Worker;

/// <summary>
/// The health-check service on a clock the test moves: it waits twenty seconds after start, then looks every fifteen seconds. The
/// full pass is claimed once every fifteen minutes and recorded completed with its next run; a check someone asked for right now runs
/// on the next look and only its own category runs; and none of it depends on the automatic-jobs switch. Real job store, real health
/// repository, real runner; the checks are two small probes that say when they ran.
/// </summary>
public sealed class TheHealthCheckServiceRunsOnItsOwnClockTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_full_pass_runs_once_per_fifteen_minutes_and_is_recorded_completed_with_its_next_run()
    {
        var (app, clock, service, probes) = await StartAsync();
        await using var _ = app;
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();

        await PumpUntilAsync(clock, () => probes.All(probe => probe.Runs >= 1), "the first full pass");
        var firstRunAt = clock.GetUtcNow();
        await service.StopAsync(CancellationToken.None);

        var state = (await jobs.ListSystemTaskStatesAsync(CancellationToken.None)).Single(item => item.ScheduleKey == SystemTasks.HealthChecks);
        Assert.Equal("completed", state.LastResult);
        Assert.Equal(state.LastCompletedUtc!.Value + TimeSpan.FromMinutes(15), state.NextRunUtc);
        Assert.NotNull(state.LastDurationMs);
        Assert.True(firstRunAt - Start >= HealthCheckHostedService_StartupDelay, "the first pass waited for the start-up delay");
        Assert.All(probes, probe => Assert.Equal(1, probe.Runs));
    }

    [Fact]
    public async Task A_check_asked_for_right_now_runs_on_the_next_look_and_only_its_own_category_runs()
    {
        var (app, clock, service, probes) = await StartAsync();
        await using var _ = app;
        await PumpUntilAsync(clock, () => probes.All(probe => probe.Runs >= 1), "the first full pass");
        var health = app.Services.GetRequiredService<IHealthCheckRepository>();

        await health.RequestImmediateRunAsync(HealthCheckCategories.Indexer, CancellationToken.None);
        await PumpUntilAsync(clock, () => probes.Single(probe => probe.Category == HealthCheckCategories.Indexer).Runs >= 2, "the asked-for check");
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(2, probes.Single(probe => probe.Category == HealthCheckCategories.Indexer).Runs);
        Assert.Equal(1, probes.Single(probe => probe.Category == HealthCheckCategories.DownloadClient).Runs);
    }

    [Fact]
    public async Task The_checks_run_whether_or_not_automatic_jobs_are_on()
    {
        var (app, clock, service, probes) = await StartAsync();
        await using var _ = app;
        await app.Services.GetRequiredService<IPlatformSettingsRepository>().SetGlobalAutomationEnabledAsync(false, CancellationToken.None);

        await PumpUntilAsync(clock, () => probes.All(probe => probe.Runs >= 1), "the first full pass with automatic jobs off");
        await service.StopAsync(CancellationToken.None);

        Assert.All(probes, probe => Assert.Equal(1, probe.Runs));
    }

    private static readonly TimeSpan HealthCheckHostedService_StartupDelay = TimeSpan.FromSeconds(20);

    private static async Task PumpUntilAsync(FakeTimeProvider clock, Func<bool> done, string what)
    {
        for (var step = 0; step < 4000 && !done(); step++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1); // reason: lets the service's loop run between one-second moves of the fake clock; the loop is bounded by 4000 steps
        }

        Assert.True(done(), $"Gave up waiting for: {what}.");
    }

    private static async Task<(ApplicationTestHost App, FakeTimeProvider Clock, HealthCheckHostedService Service, Probe[] Probes)> StartAsync()
    {
        var clock = new FakeTimeProvider(Start);
        var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<TimeProvider>(clock));
        var probes = new[] { new Probe(HealthCheckCategories.Indexer), new Probe(HealthCheckCategories.DownloadClient) };
        var services = new ServiceCollection()
            .AddSingleton<IEnumerable<IHealthCheck>>(probes)
            .AddSingleton(app.Services.GetRequiredService<IHealthCheckRepository>())
            .AddSingleton(app.Services.GetRequiredService<IActivityFeedRepository>())
            .AddSingleton(app.Services.GetRequiredService<IOutboundNotificationService>())
            .AddSingleton(app.Services.GetRequiredService<IRealtimeEventPublisher>())
            .AddSingleton<HealthCheckRunner>(provider => new HealthCheckRunner(
                provider.GetRequiredService<IEnumerable<IHealthCheck>>(),
                provider.GetRequiredService<IHealthCheckRepository>(),
                provider.GetRequiredService<IActivityFeedRepository>(),
                provider.GetRequiredService<IOutboundNotificationService>(),
                provider.GetRequiredService<IRealtimeEventPublisher>(),
                NullLogger<HealthCheckRunner>.Instance))
            .BuildServiceProvider();
        var service = new HealthCheckHostedService(
            NullLogger<HealthCheckHostedService>.Instance,
            services.GetRequiredService<IServiceScopeFactory>(),
            app.Services.GetRequiredService<IHealthCheckRepository>(),
            app.Services.GetRequiredService<IJobQueueRepository>(),
            clock);
        await service.StartAsync(CancellationToken.None);
        return (app, clock, service, probes);
    }

    private sealed class Probe(string category) : IHealthCheck
    {
        private int runs;

        public string Category => category;

        public int Runs => Volatile.Read(ref runs);

        public Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref runs);
            return Task.FromResult<IReadOnlyList<HealthCheckOutcome>>([HealthCheckOutcome.Ok($"probe:{category}", category, category, "Fine.")]);
        }
    }
}
