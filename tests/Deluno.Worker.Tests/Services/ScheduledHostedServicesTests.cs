using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Integrations.Search.Trackers;
using Deluno.Jobs.Data;
using Deluno.Quality.Guides;
using Deluno.Series.Services;
using Deluno.Worker.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// The background loops that run a pass on their own clock: each waits out its start-up delay, asks the
/// queue whether the pass is due, runs it, records how it ended (completed, cancelled or failed) and
/// keeps going when the pass throws. Driven on a fake clock so nothing waits for real minutes.
/// </summary>
public sealed class ScheduledHostedServicesTests
{
    private static async Task Until(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "The service did not get there in time.");
    }

    /// <summary>Moves the fake clock a step at a time until the service has done what is asked, so a loop that starts a moment late is still caught.</summary>
    private static async Task Advance(FakeTimeProvider time, TimeSpan step, Func<bool> done)
    {
        for (var attempt = 0; attempt < 400 && !done(); attempt++)
        {
            time.Advance(step);
            await Task.Delay(10); // reason: lets the background loop run after each step; it has no signal to await, and the loop is bounded
        }

        Assert.True(done(), "The service did not get there in time.");
    }

    private static Mock<IJobQueueRepository> Queue(bool due = true)
    {
        var queue = new Mock<IJobQueueRepository>();
        queue.Setup(q => q.TryClaimScheduledPassAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(due);
        return queue;
    }

    private static int Outcomes(Mock<IJobQueueRepository> queue, string key, string result)
        => queue.Invocations.Count(i => i.Method.Name == nameof(IJobQueueRepository.RecordScheduledPassOutcomeAsync)
            && (string)i.Arguments[0] == key && (string)i.Arguments[2] == result);

    private static Mock<IDelunoStartupGate> ReadyGate()
    {
        var gate = new Mock<IDelunoStartupGate>();
        gate.Setup(g => g.WaitAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return gate;
    }

    [Fact]
    public async Task The_scene_mapping_pass_waits_for_its_start_delay_then_runs_when_due_and_records_it()
    {
        var time = new FakeTimeProvider();
        var queue = Queue();
        var refresh = new Mock<ISceneMappingRefreshService>();
        refresh.Setup(r => r.RefreshAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new SceneMappingRefreshSummary(2, 1, 1, 3));
        using var service = new SceneMappingRefreshHostedService(refresh.Object, queue.Object, NullLogger<SceneMappingRefreshHostedService>.Instance, time);

        await service.StartAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));
        refresh.Verify(r => r.RefreshAllAsync(It.IsAny<CancellationToken>()), Times.Never);

        await Advance(time, TimeSpan.FromMinutes(1), () => Outcomes(queue, SystemTasks.SceneMappingRefresh, "completed") == 1);
        await Advance(time, TimeSpan.FromMinutes(30), () => Outcomes(queue, SystemTasks.SceneMappingRefresh, "completed") == 2);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task The_scene_mapping_pass_is_skipped_when_it_is_not_due_and_a_failure_is_recorded_and_survived()
    {
        var time = new FakeTimeProvider();
        var queue = Queue(due: false);
        var refresh = new Mock<ISceneMappingRefreshService>();
        refresh.SetupSequence(r => r.RefreshAllAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("xem is down"))
            .ReturnsAsync(new SceneMappingRefreshSummary(0, 0, 0, 0));
        using var service = new SceneMappingRefreshHostedService(refresh.Object, queue.Object, NullLogger<SceneMappingRefreshHostedService>.Instance, time);

        await service.StartAsync(CancellationToken.None);
        await Advance(time, TimeSpan.FromMinutes(2), () => queue.Invocations.Any(i => i.Method.Name == nameof(IJobQueueRepository.TryClaimScheduledPassAsync)));
        refresh.Verify(r => r.RefreshAllAsync(It.IsAny<CancellationToken>()), Times.Never);

        queue.Setup(q => q.TryClaimScheduledPassAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        await Advance(time, TimeSpan.FromMinutes(30), () => Outcomes(queue, SystemTasks.SceneMappingRefresh, "failed") == 1);
        await Advance(time, TimeSpan.FromMinutes(30), () => Outcomes(queue, SystemTasks.SceneMappingRefresh, "completed") == 1);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_scheduling_error_is_logged_and_the_loop_carries_on()
    {
        var time = new FakeTimeProvider();
        var queue = new Mock<IJobQueueRepository>();
        queue.SetupSequence(q => q.TryClaimScheduledPassAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database locked"))
            .ReturnsAsync(false);
        using var service = new SceneMappingRefreshHostedService(Mock.Of<ISceneMappingRefreshService>(), queue.Object, NullLogger<SceneMappingRefreshHostedService>.Instance, time);

        await service.StartAsync(CancellationToken.None);
        await Advance(time, TimeSpan.FromMinutes(2), () => queue.Invocations.Count(i => i.Method.Name == nameof(IJobQueueRepository.TryClaimScheduledPassAsync)) == 1);
        await Advance(time, TimeSpan.FromMinutes(30), () => queue.Invocations.Count(i => i.Method.Name == nameof(IJobQueueRepository.TryClaimScheduledPassAsync)) == 2);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stopping_during_the_start_delay_ends_the_scene_mapping_loop_quietly()
    {
        var time = new FakeTimeProvider();
        using var service = new SceneMappingRefreshHostedService(Mock.Of<ISceneMappingRefreshService>(), Queue().Object, NullLogger<SceneMappingRefreshHostedService>.Instance, time);
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(200); // reason: lets the loop reach its wait before it is stopped; there is no signal to await
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(TaskStatus.RanToCompletion, service.ExecuteTask!.Status);
    }

    [Fact]
    public async Task The_episode_group_pass_resolves_its_service_per_pass_and_records_the_outcome()
    {
        var time = new FakeTimeProvider();
        var queue = Queue();
        var refresh = new Mock<IEpisodeGroupRefreshService>();
        refresh.SetupSequence(r => r.RefreshAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EpisodeGroupRefreshSummary(3, 1, 2, 10))
            .ThrowsAsync(new InvalidOperationException("tmdb is down"));
        var services = new ServiceCollection().AddScoped(_ => refresh.Object).BuildServiceProvider();
        using var service = new EpisodeGroupRefreshHostedService(
            services.GetRequiredService<IServiceScopeFactory>(), queue.Object, NullLogger<EpisodeGroupRefreshHostedService>.Instance, time);

        await service.StartAsync(CancellationToken.None);
        await Advance(time, TimeSpan.FromMinutes(3), () => Outcomes(queue, SystemTasks.EpisodeGroupRefresh, "completed") == 1);
        await Advance(time, TimeSpan.FromMinutes(30), () => Outcomes(queue, SystemTasks.EpisodeGroupRefresh, "failed") == 1);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task The_episode_group_pass_is_skipped_when_not_due_and_stopping_during_the_delay_is_quiet()
    {
        var time = new FakeTimeProvider();
        var queue = Queue(due: false);
        var services = new ServiceCollection().BuildServiceProvider();
        using var service = new EpisodeGroupRefreshHostedService(
            services.GetRequiredService<IServiceScopeFactory>(), queue.Object, NullLogger<EpisodeGroupRefreshHostedService>.Instance, time);
        await service.StartAsync(CancellationToken.None);
        await Advance(time, TimeSpan.FromMinutes(3), () => queue.Invocations.Any(i => i.Method.Name == nameof(IJobQueueRepository.TryClaimScheduledPassAsync)));
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(0, Outcomes(queue, SystemTasks.EpisodeGroupRefresh, "completed"));

        using var early = new EpisodeGroupRefreshHostedService(
            services.GetRequiredService<IServiceScopeFactory>(), queue.Object, NullLogger<EpisodeGroupRefreshHostedService>.Instance, new FakeTimeProvider());
        await early.StartAsync(CancellationToken.None);
        await Task.Delay(200); // reason: lets the loop reach its wait before it is stopped; there is no signal to await
        await early.StopAsync(CancellationToken.None);
        Assert.Equal(TaskStatus.RanToCompletion, early.ExecuteTask!.Status);
    }

    [Fact]
    public async Task The_regular_connection_check_tests_the_stale_connections_and_records_how_it_ended()
    {
        var time = new FakeTimeProvider();
        var queue = Queue();
        var tester = new Mock<IConnectionHealthTester>();
        tester.SetupSequence(t => t.TestStaleAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([])
            .ThrowsAsync(new InvalidOperationException("boom"));
        var services = new ServiceCollection().AddScoped(_ => tester.Object).BuildServiceProvider();
        using var service = new RegularConnectionCheckService(
            NullLogger<RegularConnectionCheckService>.Instance, services.GetRequiredService<IServiceScopeFactory>(), queue.Object, time);

        await service.StartAsync(CancellationToken.None);
        await Advance(time, TimeSpan.FromSeconds(45), () => Outcomes(queue, SystemTasks.ConnectionsCheck, "completed") == 1);
        await Advance(time, TimeSpan.FromSeconds(30), () => Outcomes(queue, SystemTasks.ConnectionsCheck, "failed") == 1);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task The_regular_connection_check_does_nothing_when_another_pass_ran_this_minute_and_records_a_cancel()
    {
        var queue = Queue(due: false);
        var services = new ServiceCollection().BuildServiceProvider();
        using var service = new RegularConnectionCheckService(
            NullLogger<RegularConnectionCheckService>.Instance, services.GetRequiredService<IServiceScopeFactory>(), queue.Object, new FakeTimeProvider());
        await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, Outcomes(queue, SystemTasks.ConnectionsCheck, "completed"));

        var due = Queue();
        var tester = new Mock<IConnectionHealthTester>();
        using var cts = new CancellationTokenSource();
        tester.Setup(t => t.TestStaleAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(() => { cts.Cancel(); throw new OperationCanceledException(cts.Token); });
        var withTester = new ServiceCollection().AddScoped(_ => tester.Object).BuildServiceProvider();
        using var cancelled = new RegularConnectionCheckService(
            NullLogger<RegularConnectionCheckService>.Instance, withTester.GetRequiredService<IServiceScopeFactory>(), due.Object, new FakeTimeProvider());
        await cancelled.RunOnceAsync(cts.Token);
        Assert.Equal(1, Outcomes(due, SystemTasks.ConnectionsCheck, "cancelled"));
    }

    [Fact]
    public async Task Stopping_during_the_start_delay_ends_the_connection_check_loop_quietly()
    {
        using var service = new RegularConnectionCheckService(
            NullLogger<RegularConnectionCheckService>.Instance,
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Queue().Object,
            new FakeTimeProvider());
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(200); // reason: lets the loop reach its wait before it is stopped; there is no signal to await
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(TaskStatus.RanToCompletion, service.ExecuteTask!.Status);
    }

    [Fact]
    public async Task The_tracker_definition_update_runs_once_the_databases_are_ready_and_survives_a_failure()
    {
        var update = new Mock<ITrackerDefinitionUpdateService>();
        var runs = 0;
        update.Setup(u => u.RunIfDueAsync(It.IsAny<CancellationToken>()))
            .Returns(() => ++runs == 1 ? Task.FromException<TrackerDefinitionUpdateState>(new InvalidOperationException("github is down")) : Task.FromResult<TrackerDefinitionUpdateState>(null!));
        using var service = new TrackerDefinitionAutoUpdateHostedService(update.Object, ReadyGate().Object, NullLogger<TrackerDefinitionAutoUpdateHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Until(() => runs >= 1);
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(TaskStatus.RanToCompletion, service.ExecuteTask!.Status);
    }

    [Fact]
    public async Task The_guide_update_check_runs_once_the_databases_are_ready_and_survives_a_failure()
    {
        var check = new Mock<IGuideUpdateCheckService>();
        var runs = 0;
        check.Setup(c => c.RunIfDueAsync(It.IsAny<CancellationToken>()))
            .Returns(() => { runs++; return Task.FromException<GuideUpdateCheckState>(new InvalidOperationException("host down")); });
        using var service = new GuideUpdateCheckHostedService(check.Object, ReadyGate().Object, NullLogger<GuideUpdateCheckHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Until(() => runs >= 1);
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(TaskStatus.RanToCompletion, service.ExecuteTask!.Status);
    }

    [Fact]
    public async Task Neither_update_loop_runs_when_stopped_before_the_databases_are_ready()
    {
        var gate = new Mock<IDelunoStartupGate>();
        gate.Setup(g => g.WaitAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns<TimeSpan, CancellationToken>((_, token) => Task.Delay(Timeout.Infinite, token));
        var update = new Mock<ITrackerDefinitionUpdateService>(MockBehavior.Strict);
        var check = new Mock<IGuideUpdateCheckService>(MockBehavior.Strict);
        using var tracker = new TrackerDefinitionAutoUpdateHostedService(update.Object, gate.Object, NullLogger<TrackerDefinitionAutoUpdateHostedService>.Instance);
        using var guide = new GuideUpdateCheckHostedService(check.Object, gate.Object, NullLogger<GuideUpdateCheckHostedService>.Instance);

        await tracker.StartAsync(CancellationToken.None);
        await guide.StartAsync(CancellationToken.None);
        await Task.Delay(200); // reason: lets the loop reach its wait before it is stopped; there is no signal to await
        await tracker.StopAsync(CancellationToken.None);
        await guide.StopAsync(CancellationToken.None);
        Assert.Equal(TaskStatus.RanToCompletion, tracker.ExecuteTask!.Status);
        Assert.Equal(TaskStatus.RanToCompletion, guide.ExecuteTask!.Status);
    }
}
