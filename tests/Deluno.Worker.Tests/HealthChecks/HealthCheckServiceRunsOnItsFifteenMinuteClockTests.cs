using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Notifications;
using Deluno.Worker.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks;

/// <summary>
/// Coverage register, area services: the health check service runs every check on the fifteen-minute clock (claiming the pass with
/// the declared fifteen-minute interval, recording its outcome so System Tasks can read it) and runs only the categories a changed
/// setting asked for at once, without waiting. The real runner, a fake clock, and a claim that is due only when the clock says so.
/// </summary>
public sealed class HealthCheckServiceRunsOnItsFifteenMinuteClockTests
{
    private sealed class CountingCheck(string category) : IHealthCheck
    {
        private int _runs;
        public string Category => category;
        public int Runs => Volatile.Read(ref _runs);

        public Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _runs);
            return Task.FromResult<IReadOnlyList<HealthCheckOutcome>>([]);
        }
    }

    /// <summary>A schedule table in memory: the claim is granted only when the interval since the last claim has passed.</summary>
    private sealed class Schedule(FakeTimeProvider time)
    {
        private DateTimeOffset? _lastClaim;
        public readonly List<(string Key, TimeSpan Interval)> Claims = [];
        public readonly List<(string Key, string Result, DateTimeOffset Completed, DateTimeOffset Next)> Outcomes = [];

        public Mock<IJobQueueRepository> Jobs()
        {
            var jobs = new Mock<IJobQueueRepository>();
            jobs.Setup(item => item.TryClaimScheduledPassAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string key, TimeSpan interval, CancellationToken _) =>
                {
                    var now = time.GetUtcNow();
                    if (_lastClaim is { } last && now - last < interval) return false;
                    _lastClaim = now;
                    Claims.Add((key, interval));
                    return true;
                });
            jobs.Setup(item => item.RecordScheduledPassOutcomeAsync(
                    It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
                .Returns((string key, DateTimeOffset completed, string result, long _, DateTimeOffset? next, CancellationToken _, string? _) =>
                {
                    Outcomes.Add((key, result, completed, next!.Value));
                    return Task.CompletedTask;
                });
            return jobs;
        }
    }

    private static (HealthCheckHostedService Service, Mock<IHealthCheckRepository> Repository, Schedule Schedule) Build(FakeTimeProvider time, params IHealthCheck[] checks)
    {
        var repository = new Mock<IHealthCheckRepository>();
        repository.Setup(item => item.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<HealthCheckResultItem>());
        repository.Setup(item => item.TakePendingImmediateRunsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<string>());
        repository.Setup(item => item.UpsertResultsAsync(It.IsAny<IReadOnlyList<HealthCheckOutcome>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<HealthCheckTransitionResult>());

        var runner = new HealthCheckRunner(
            checks, repository.Object, Mock.Of<IActivityFeedRepository>(), Mock.Of<IOutboundNotificationService>(),
            Mock.Of<IRealtimeEventPublisher>(), NullLogger<HealthCheckRunner>.Instance);
        var services = new ServiceCollection().AddSingleton(runner).BuildServiceProvider();
        var schedule = new Schedule(time);

        return (new HealthCheckHostedService(
            NullLogger<HealthCheckHostedService>.Instance,
            services.GetRequiredService<IServiceScopeFactory>(),
            repository.Object,
            schedule.Jobs().Object,
            time), repository, schedule);
    }

    private static async Task<bool> AdvanceUntilAsync(FakeTimeProvider time, Func<bool> done, TimeSpan limit)
    {
        var step = TimeSpan.FromSeconds(15);
        for (var moved = TimeSpan.Zero; moved < limit; moved += step)
        {
            time.Advance(step);
            await Task.Delay(10); // reason: lets the background service run after each fake-clock step; it has no signal to await, and the loop is bounded by limit
            if (done()) return true;
        }

        return done();
    }

    [Fact]
    public async Task Every_check_runs_soon_after_start_and_again_only_when_fifteen_minutes_are_up_and_the_outcome_is_recorded_for_System_Tasks()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T08:00:00Z"));
        var check = new CountingCheck(HealthCheckCategories.Metadata);
        var (service, _, schedule) = Build(time, check);

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await AdvanceUntilAsync(time, () => check.Runs >= 1, TimeSpan.FromMinutes(2)), "No health pass ran soon after start.");
            Assert.True(await AdvanceUntilAsync(time, () => schedule.Outcomes.Count >= 1, TimeSpan.FromSeconds(30)));

            var outcome = schedule.Outcomes.Single();
            Assert.Equal(SystemTasks.HealthChecks, outcome.Key);
            Assert.Equal("completed", outcome.Result);
            Assert.Equal(TimeSpan.FromMinutes(15), outcome.Next - outcome.Completed);
            Assert.Equal(TimeSpan.FromMinutes(15), schedule.Claims.Single().Interval);

            Assert.False(await AdvanceUntilAsync(time, () => check.Runs >= 2, TimeSpan.FromMinutes(14)), "The full pass ran again before fifteen minutes were up.");
            Assert.True(await AdvanceUntilAsync(time, () => check.Runs >= 2, TimeSpan.FromMinutes(2)), "The full pass did not run again once due.");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_changed_setting_runs_only_its_own_categories_at_once_without_waiting_for_the_fifteen_minutes()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T08:00:00Z"));
        var metadata = new CountingCheck(HealthCheckCategories.Metadata);
        var clients = new CountingCheck(HealthCheckCategories.DownloadClient);
        var (service, repository, _) = Build(time, metadata, clients);

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await AdvanceUntilAsync(time, () => metadata.Runs >= 1 && clients.Runs >= 1, TimeSpan.FromMinutes(2)));

            repository.SetupSequence(item => item.TakePendingImmediateRunsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { HealthCheckCategories.Metadata })
                .ReturnsAsync(Array.Empty<string>());

            Assert.True(await AdvanceUntilAsync(time, () => metadata.Runs >= 2, TimeSpan.FromSeconds(60)), "The changed setting check did not run at once.");
            Assert.Equal(1, clients.Runs); // only the asked-for category; the rest wait for the fifteen minutes
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}
