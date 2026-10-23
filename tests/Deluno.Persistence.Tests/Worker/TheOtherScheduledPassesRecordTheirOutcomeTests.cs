using System.Reflection;
using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Data;
using Deluno.Worker.Intake;
using Deluno.Worker.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Worker;

/// <summary>
/// The planning lanes' other scheduled passes (library file check, list sync, import resume, metadata top-up, media probe), run by the
/// real <see cref="WorkPlanner"/> on the real job store and a clock the test moves, the way the maintenance passes are in
/// <see cref="TheMaintenancePassesRecordTheirOutcomeTests"/>: a pass whose dependency fails is recorded failed in plain words and
/// does not throw out of the lane tick, one stopped by shutdown is recorded cancelled, and the next interval tries again. Refs #1296.
/// </summary>
public sealed class TheOtherScheduledPassesRecordTheirOutcomeTests
{
    private static readonly TimeSpan PastEveryInterval = TimeSpan.FromDays(10);

    public static TheoryData<string> Passes => new()
    {
        SystemTasks.LibraryFileCheck,
        SystemTasks.IntakeAutomation,
        SystemTasks.LibraryImportResume,
        SystemTasks.MetadataRefresh,
        SystemTasks.MediaProbe
    };

    /// <summary>A stand-in for a service the pass calls: the named member fails on request, every other member answers with an empty, finished result.</summary>
    internal class Stub : DispatchProxy
    {
        public string FailingMember { get; set; } = string.Empty;

        public Func<Exception>? Failure { get; set; }

        public Action? OnCall { get; set; }

        public int Calls { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var returns = targetMethod!.ReturnType;
            if (targetMethod.Name == FailingMember)
            {
                Calls++;
                OnCall?.Invoke();
                if (Failure?.Invoke() is { } failure)
                {
                    return returns.IsGenericType
                        ? typeof(Task).GetMethod(nameof(Task.FromException), 1, [typeof(Exception)])!.MakeGenericMethod(returns.GenericTypeArguments[0]).Invoke(null, [failure])
                        : Task.FromException(failure);
                }
            }

            if (!returns.IsGenericType)
            {
                return Task.CompletedTask;
            }

            var result = returns.GenericTypeArguments[0];
            object? value = result.IsGenericType && (result.GetGenericTypeDefinition() == typeof(IReadOnlyList<>) || result.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                ? Array.CreateInstance(result.GenericTypeArguments[0], 0)
                : result.IsValueType ? Activator.CreateInstance(result) : result == typeof(LibraryFileCheckResult) ? new LibraryFileCheckResult(0, 0) : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(result).Invoke(null, [value]);
        }
    }

    private static T StubOf<T>(string failingMember, Func<Exception>? failure = null, Action? onCall = null) where T : class
    {
        var proxy = DispatchProxy.Create<T, Stub>();
        var stub = (Stub)(object)proxy;
        stub.FailingMember = failingMember;
        stub.Failure = failure;
        stub.OnCall = onCall;
        return proxy;
    }

    private static int CallsOf(object proxy) => ((Stub)proxy).Calls;

    private sealed record Harness(ApplicationTestHost App, WorkPlanner Planner, FixedTimeProvider Clock) : IAsyncDisposable
    {
        public IJobQueueRepository Jobs => App.Services.GetRequiredService<IJobQueueRepository>();

        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    private static async Task<Harness> StartAsync()
    {
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<TimeProvider>(clock));
        var planner = new WorkPlanner(NullLogger<WorkPlanner>.Instance, app.Services.GetRequiredService<IJobQueueRepository>(), new ConfigurationBuilder().Build(), clock);
        return new Harness(app, planner, clock);
    }

    /// <summary>Runs the pass once. The dependency is the one named stand-in; what the pass does not call is the real thing.</summary>
    private static Task RunAsync(Harness harness, string pass, object dependency, CancellationToken cancellationToken) => pass switch
    {
        SystemTasks.LibraryFileCheck => harness.Planner.RunLibraryFileCheckAsync((ILibraryFileCheckService)dependency, 6, cancellationToken),
        SystemTasks.IntakeAutomation => harness.Planner.PlanIntakeAutomationAsync((IIntakeSyncService)dependency, cancellationToken),
        SystemTasks.LibraryImportResume => harness.Planner.PlanLibraryImportResumeAsync(
            harness.App.Services.GetRequiredService<IJobScheduler>(), (IExistingLibraryImportService)dependency, harness.Clock, cancellationToken),
        SystemTasks.MetadataRefresh => harness.Planner.PlanMetadataRefreshAutomationAsync(
            harness.App.Services.GetRequiredService<IJobScheduler>(), (IMovieCatalogRepository)dependency, harness.App.Services.GetRequiredService<ISeriesCatalogRepository>(), harness.Clock, cancellationToken),
        SystemTasks.MediaProbe => harness.Planner.PlanMediaProbeAsync((IJobScheduler)dependency, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(pass), pass, "No test runner for this pass.")
    };

    private static object Dependency(string pass, Func<Exception>? failure = null, Action? onCall = null) => pass switch
    {
        SystemTasks.LibraryFileCheck => StubOf<ILibraryFileCheckService>(nameof(ILibraryFileCheckService.RunAsync), failure, onCall),
        SystemTasks.IntakeAutomation => StubOf<IIntakeSyncService>(nameof(IIntakeSyncService.PlanDueSyncJobsAsync), failure, onCall),
        SystemTasks.LibraryImportResume => StubOf<IExistingLibraryImportService>(nameof(IExistingLibraryImportService.ListResumableRunsAsync), failure, onCall),
        SystemTasks.MetadataRefresh => StubOf<IMovieCatalogRepository>(nameof(IMovieCatalogRepository.ListStaleMetadataCandidatesAsync), failure, onCall),
        SystemTasks.MediaProbe => StubOf<IJobScheduler>(nameof(IJobScheduler.EnqueueAsync), failure, onCall),
        _ => throw new ArgumentOutOfRangeException(nameof(pass), pass, "No stand-in for this pass.")
    };

    private static async Task<SystemTaskState> StateAsync(Harness harness, string key)
        => (await harness.Jobs.ListSystemTaskStatesAsync(CancellationToken.None)).Single(state => state.ScheduleKey == key);

    [Theory]
    [MemberData(nameof(Passes))]
    public async Task A_pass_whose_dependency_fails_is_recorded_failed_in_plain_words_without_throwing_and_the_next_interval_tries_again(string pass)
    {
        await using var harness = await StartAsync();
        var failing = Dependency(pass, () => new InvalidOperationException("the dependency broke in a way only a developer would word"));

        await RunAsync(harness, pass, failing, CancellationToken.None);

        var failed = await StateAsync(harness, pass);
        Assert.Equal("failed", failed.LastResult);
        Assert.False(string.IsNullOrWhiteSpace(failed.LastError));
        Assert.DoesNotContain("InvalidOperationException", failed.LastError);

        // Inside its interval the pass is not asked again; once the interval has passed it runs, and this time it works.
        await RunAsync(harness, pass, failing, CancellationToken.None);
        Assert.Equal(1, CallsOf(failing));
        harness.Clock.Advance(PastEveryInterval);
        var working = Dependency(pass);

        await RunAsync(harness, pass, working, CancellationToken.None);

        Assert.True(CallsOf(working) >= 1, "the pass asked its dependency again once the interval had passed");
        var recovered = await StateAsync(harness, pass);
        Assert.Equal("completed", recovered.LastResult);
        Assert.True(string.IsNullOrWhiteSpace(recovered.LastError));
        Assert.True(recovered.NextRunUtc > harness.Clock.GetUtcNow());
    }

    [Theory]
    [MemberData(nameof(Passes))]
    public async Task A_pass_stopped_by_shutdown_is_recorded_cancelled_not_failed(string pass)
    {
        await using var harness = await StartAsync();
        using var stopping = new CancellationTokenSource();
        var stopped = Dependency(pass, () => new OperationCanceledException(stopping.Token), () => stopping.Cancel());

        await RunAsync(harness, pass, stopped, stopping.Token);

        Assert.Equal("cancelled", (await StateAsync(harness, pass)).LastResult);
    }

    [Fact]
    public async Task The_media_probe_pass_queues_one_slice_for_movies_and_one_for_shows_each_under_its_own_dedupe_key_and_a_second_pass_stacks_nothing()
    {
        await using var harness = await StartAsync();
        var scheduler = harness.App.Services.GetRequiredService<IJobScheduler>();

        await harness.Planner.PlanMediaProbeAsync(scheduler, CancellationToken.None);
        harness.Clock.Advance(PastEveryInterval);
        await harness.Planner.PlanMediaProbeAsync(scheduler, CancellationToken.None);

        Assert.Equal("completed", (await StateAsync(harness, SystemTasks.MediaProbe)).LastResult);
        Assert.Equal(2, await ApiScenes.ScalarAsync(harness.App, "jobs", "SELECT COUNT(*) FROM job_queue WHERE job_type = 'library.media.probe';"));
        Assert.Equal("media-probe:movie|media-probe:series", await ApiScenes.TextAsync(harness.App, "jobs", "SELECT group_concat(dedupe_key, '|') FROM (SELECT dedupe_key FROM job_queue WHERE job_type = 'library.media.probe' ORDER BY dedupe_key);"));
    }
}
