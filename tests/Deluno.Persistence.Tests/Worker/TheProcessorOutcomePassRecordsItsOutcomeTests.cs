using System.Reflection;
using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Worker.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Worker;

/// <summary>
/// The processor-outcome pass (every minute, in the planning.maintenance lane): a dependency or service call that fails is
/// recorded failed in plain words and does not throw out of the lane tick, shutdown is recorded cancelled, and the next
/// interval tries again. Run by the real planner on the real job store.
/// </summary>
public sealed class TheProcessorOutcomePassRecordsItsOutcomeTests
{
    private sealed record Source(ProcessorOutcomeReporter Reporter, TheOtherScheduledPassesRecordTheirOutcomeTests.Stub Stub);

    private static Source Reporter(Func<Exception>? failure, Action? onCall = null)
    {
        var processors = DispatchProxy.Create<IProcessorRepository, TheOtherScheduledPassesRecordTheirOutcomeTests.Stub>();
        var stub = (TheOtherScheduledPassesRecordTheirOutcomeTests.Stub)(object)processors;
        stub.FailingMember = nameof(IProcessorRepository.ListDueProcessorOutcomesAsync);
        stub.Failure = failure;
        stub.OnCall = onCall;
        return new Source(
            new ProcessorOutcomeReporter(
                processors,
                DispatchProxy.Create<IProcessorConnectionService, TheOtherScheduledPassesRecordTheirOutcomeTests.Stub>(),
                null!,
                TimeProvider.System,
                NullLogger<ProcessorOutcomeReporter>.Instance),
            stub);
    }

    private static async Task<(ApplicationTestHost App, WorkPlanner Planner, FixedTimeProvider Clock)> StartAsync()
    {
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<TimeProvider>(clock));
        var planner = new WorkPlanner(NullLogger<WorkPlanner>.Instance, app.Services.GetRequiredService<IJobQueueRepository>(), new ConfigurationBuilder().Build(), clock);
        return (app, planner, clock);
    }

    private static async Task<SystemTaskState> StateAsync(ApplicationTestHost app)
        => (await app.Services.GetRequiredService<IJobQueueRepository>().ListSystemTaskStatesAsync(CancellationToken.None))
            .Single(state => state.ScheduleKey == SystemTasks.ProcessorOutcomes);

    [Fact]
    public async Task A_call_that_fails_is_recorded_failed_in_plain_words_and_the_next_interval_tries_again()
    {
        var (app, planner, clock) = await StartAsync();
        await using var _ = app;
        var failing = Reporter(() => new InvalidOperationException("the processor list broke in a way only a developer would word"));

        await planner.RunProcessorOutcomePassAsync(failing.Reporter, CancellationToken.None);

        var failed = await StateAsync(app);
        Assert.Equal("failed", failed.LastResult);
        Assert.False(string.IsNullOrWhiteSpace(failed.LastError));
        Assert.DoesNotContain("InvalidOperationException", failed.LastError);

        await planner.RunProcessorOutcomePassAsync(failing.Reporter, CancellationToken.None);
        Assert.Equal(1, failing.Stub.Calls);

        clock.Advance(TimeSpan.FromHours(1));
        var working = Reporter(null);
        await planner.RunProcessorOutcomePassAsync(working.Reporter, CancellationToken.None);

        Assert.Equal(1, working.Stub.Calls);
        var recovered = await StateAsync(app);
        Assert.Equal("completed", recovered.LastResult);
        Assert.Null(recovered.LastError);
        Assert.Equal(clock.GetUtcNow() + SystemTasks.IntervalFor(SystemTasks.ProcessorOutcomes), recovered.NextRunUtc);
    }

    [Fact]
    public async Task A_pass_stopped_by_shutdown_is_recorded_cancelled_not_failed()
    {
        var (app, planner, _) = await StartAsync();
        await using var _ = app;
        using var stopping = new CancellationTokenSource();
        var stopped = Reporter(() => new OperationCanceledException(stopping.Token), () => stopping.Cancel());

        await planner.RunProcessorOutcomePassAsync(stopped.Reporter, stopping.Token);

        Assert.Equal("cancelled", (await StateAsync(app)).LastResult);
    }
}
