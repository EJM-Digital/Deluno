using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Metadata;
using Deluno.Movies.Data;
using Deluno.Series.Data;
using Deluno.Jobs.Data;
using Deluno.Media;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Deluno.Recovery.Contracts;
using Deluno.Worker.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Worker;

/// <summary>
/// The planning.maintenance lane's scheduled passes, run by the real <see cref="WorkPlanner"/> against the real job store (a real
/// SQLite file) and a clock the test moves: a pass that works is recorded completed, one whose service throws is recorded failed with
/// a plain reason and does not stop the next pass, a pass that has run is not claimed again inside its interval, and it is tried
/// again once the interval has passed.
/// </summary>
public sealed class TheMaintenancePassesRecordTheirOutcomeTests
{
    private static readonly TimeSpan PastEveryInterval = TimeSpan.FromDays(2);

    public static TheoryData<string> Passes => new()
    {
        SystemTasks.DispatchCleanup,
        SystemTasks.DownloadState,
        SystemTasks.RecycleBinCleanup,
        SystemTasks.DownloadLeftoverCleanup,
        SystemTasks.BlockedReleaseCleanup,
        SystemTasks.UnmatchedImportGiveUp
    };

    [Theory]
    [MemberData(nameof(Passes))]
    public async Task A_pass_that_works_is_recorded_completed_with_its_next_run_and_is_not_claimed_again_inside_its_interval(string pass)
    {
        var (app, planner, clock, service) = await StartAsync();
        await using var _ = app;
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();

        await RunAsync(planner, pass, service);
        await RunAsync(planner, pass, service);

        Assert.Equal(1, service.Calls);
        var state = await StateAsync(jobs, pass);
        Assert.Equal("completed", state.LastResult);
        Assert.Null(state.LastError);
        Assert.Equal(clock.GetUtcNow() + SystemTasks.IntervalFor(pass), state.NextRunUtc);
    }

    [Theory]
    [MemberData(nameof(Passes))]
    public async Task A_pass_whose_service_throws_is_recorded_failed_in_plain_words_and_the_next_interval_tries_again(string pass)
    {
        var (app, planner, clock, service) = await StartAsync();
        await using var _ = app;
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        service.Throw = new InvalidOperationException("the service broke in a way only a developer would word");

        await RunAsync(planner, pass, service);

        var failed = await StateAsync(jobs, pass);
        Assert.Equal("failed", failed.LastResult);
        Assert.False(string.IsNullOrWhiteSpace(failed.LastError));

        // Inside the interval nothing is retried; once it has passed the same pass runs again and, this time, works.
        await RunAsync(planner, pass, service);
        Assert.Equal(1, service.Calls);
        clock.Advance(PastEveryInterval);
        service.Throw = null;
        await RunAsync(planner, pass, service);

        Assert.Equal(2, service.Calls);
        var recovered = await StateAsync(jobs, pass);
        Assert.Equal("completed", recovered.LastResult);
        Assert.Null(recovered.LastError);
    }

    [Fact]
    public async Task A_pass_that_fails_does_not_stop_the_next_pass_on_the_lane()
    {
        var (app, planner, _, broken) = await StartAsync();
        await using var _ = app;
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        broken.Throw = new InvalidOperationException("boom");
        var healthy = new CountingService();

        await RunAsync(planner, SystemTasks.RecycleBinCleanup, broken);
        await RunAsync(planner, SystemTasks.BlockedReleaseCleanup, healthy);

        Assert.Equal("failed", (await StateAsync(jobs, SystemTasks.RecycleBinCleanup)).LastResult);
        Assert.Equal("completed", (await StateAsync(jobs, SystemTasks.BlockedReleaseCleanup)).LastResult);
        Assert.Equal(1, healthy.Calls);
    }

    [Fact]
    public async Task A_pass_stopped_by_shutdown_is_recorded_cancelled_not_failed()
    {
        var (app, planner, _, service) = await StartAsync();
        await using var _ = app;
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        using var stopping = new CancellationTokenSource();
        service.OnCall = () => stopping.Cancel();
        service.Throw = new OperationCanceledException(stopping.Token);

        await planner.RunRecycleBinCleanupAsync(service.AsRecycleBin(), stopping.Token);

        Assert.Equal("cancelled", (await StateAsync(jobs, SystemTasks.RecycleBinCleanup)).LastResult);
    }

    [Fact]
    public async Task An_artwork_cleanup_whose_catalogue_cannot_be_read_is_recorded_failed_in_plain_words_and_the_next_interval_tries_again()
    {
        var (app, planner, clock, _) = await StartAsync();
        await using var _ = app;
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var movies = ThrowingOn<IMovieCatalogRepository>(nameof(IMovieCatalogRepository.ListReferencedArtworkCacheKeysAsync));

        await planner.RunArtworkCacheCleanupAsync(
            app.Services.GetRequiredService<TmdbMetadataProvider>(), movies, app.Services.GetRequiredService<ISeriesCatalogRepository>(),
            clock, app.Services.GetRequiredService<IActivityFeedRepository>(), CancellationToken.None);

        var failed = await StateAsync(jobs, SystemTasks.ArtworkCacheCleanup);
        Assert.Equal("failed", failed.LastResult);
        Assert.False(string.IsNullOrWhiteSpace(failed.LastError));
        Assert.DoesNotContain("InvalidOperationException", failed.LastError);

        clock.Advance(PastEveryInterval);
        await planner.RunArtworkCacheCleanupAsync(
            app.Services.GetRequiredService<TmdbMetadataProvider>(), app.Services.GetRequiredService<IMovieCatalogRepository>(), app.Services.GetRequiredService<ISeriesCatalogRepository>(),
            clock, app.Services.GetRequiredService<IActivityFeedRepository>(), CancellationToken.None);
        var recovered = await StateAsync(jobs, SystemTasks.ArtworkCacheCleanup);
        Assert.Equal("completed", recovered.LastResult);
        Assert.Null(recovered.LastError);
    }

    [Fact]
    public async Task An_artwork_cleanup_stopped_by_shutdown_is_recorded_cancelled()
    {
        var (app, planner, clock, _) = await StartAsync();
        await using var _ = app;
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        using var stopping = new CancellationTokenSource();
        var movies = ThrowingOn<IMovieCatalogRepository>(nameof(IMovieCatalogRepository.ListReferencedArtworkCacheKeysAsync), () => new OperationCanceledException(stopping.Token), stopping.Cancel);

        await planner.RunArtworkCacheCleanupAsync(
            app.Services.GetRequiredService<TmdbMetadataProvider>(), movies, app.Services.GetRequiredService<ISeriesCatalogRepository>(),
            clock, app.Services.GetRequiredService<IActivityFeedRepository>(), stopping.Token);

        Assert.Equal("cancelled", (await StateAsync(jobs, SystemTasks.ArtworkCacheCleanup)).LastResult);
    }

    /// <summary>A stand-in for a big repository interface: the one method named throws (as a faulted task), anything else is not used by the pass.</summary>
    private static T ThrowingOn<T>(string method, Func<Exception>? exception = null, Action? beforeThrowing = null) where T : class
    {
        var proxy = System.Reflection.DispatchProxy.Create<T, ThrowingProxy>();
        var typed = (ThrowingProxy)(object)proxy;
        typed.Method = method;
        typed.Exception = exception ?? (() => new InvalidOperationException("the catalogue could not be read"));
        typed.BeforeThrowing = beforeThrowing;
        return proxy;
    }

    internal class ThrowingProxy : System.Reflection.DispatchProxy
    {
        public string Method { get; set; } = string.Empty;

        public Func<Exception> Exception { get; set; } = () => new InvalidOperationException();

        public Action? BeforeThrowing { get; set; }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != Method)
            {
                throw new NotSupportedException(targetMethod?.Name);
            }

            BeforeThrowing?.Invoke();
            var result = targetMethod.ReturnType.GenericTypeArguments[0];
            var fromException = typeof(Task).GetMethod(nameof(Task.FromException), 1, [typeof(Exception)])!.MakeGenericMethod(result);
            return fromException.Invoke(null, [Exception()]);
        }
    }

    private static async Task<(ApplicationTestHost App, WorkPlanner Planner, FixedTimeProvider Clock, CountingService Service)> StartAsync()
    {
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<TimeProvider>(clock));
        var planner = new WorkPlanner(
            NullLogger<WorkPlanner>.Instance,
            app.Services.GetRequiredService<IJobQueueRepository>(),
            new ConfigurationBuilder().Build(),
            clock);
        return (app, planner, clock, new CountingService());
    }

    private static Task RunAsync(WorkPlanner planner, string pass, CountingService service) => pass switch
    {
        SystemTasks.DispatchCleanup => planner.RunDispatchCleanupAsync(service, CancellationToken.None),
        SystemTasks.DownloadState => planner.RunDownloadStateReconcileAsync(service, CancellationToken.None),
        SystemTasks.RecycleBinCleanup => planner.RunRecycleBinCleanupAsync(service.AsRecycleBin(), CancellationToken.None),
        SystemTasks.DownloadLeftoverCleanup => planner.RunLeftoverDownloadCleanupAsync(service, CancellationToken.None),
        SystemTasks.BlockedReleaseCleanup => planner.RunBlockedReleaseCleanupAsync(service, CancellationToken.None),
        SystemTasks.UnmatchedImportGiveUp => planner.RunUnmatchedImportGiveUpAsync(service, CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(nameof(pass), pass, "No test runner for this pass.")
    };

    private static async Task<SystemTaskState> StateAsync(IJobQueueRepository jobs, string key)
        => (await jobs.ListSystemTaskStatesAsync(CancellationToken.None)).Single(state => state.ScheduleKey == key);

    /// <summary>One stand-in for each service a maintenance pass calls: it counts the calls and throws on request.</summary>
    internal sealed class CountingService
        : IDispatchCleanupService, IDownloadStateReconciler, ILeftoverDownloadCleanupService, IRefusedDownloadCleanupService, IUnmatchedImportGiveUpService
    {
        public int Calls { get; private set; }

        public void CalledByARecycleBinCleanup() => Called();

        public Exception? Throw { get; set; }

        public Action? OnCall { get; set; }

        internal void Called()
        {
            Calls++;
            OnCall?.Invoke();
            if (Throw is not null)
            {
                throw Throw;
            }
        }

        Task<DispatchCleanupResult> IDispatchCleanupService.RunCleanupPassAsync(CancellationToken cancellationToken)
        {
            Called();
            return Task.FromResult(new DispatchCleanupResult(0, 0, "Nothing to archive."));
        }

        Task<int> IDownloadStateReconciler.ReconcileAsync(CancellationToken cancellationToken)
        {
            Called();
            return Task.FromResult(0);
        }

        Task<LeftoverDownloadCleanupResult> ILeftoverDownloadCleanupService.RunAsync(CancellationToken cancellationToken)
        {
            Called();
            return Task.FromResult(new LeftoverDownloadCleanupResult(0, 0));
        }

        Task<RefusedDownloadCleanupResult> IRefusedDownloadCleanupService.CleanUpEverythingAsync(CancellationToken cancellationToken)
        {
            Called();
            return Task.FromResult(new RefusedDownloadCleanupResult(0, 0, 0));
        }

        Task<string> IRefusedDownloadCleanupService.CleanUpOneAsync(string blockedReleaseId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        Task<int> IUnmatchedImportGiveUpService.RunAsync(CancellationToken cancellationToken)
        {
            Called();
            return Task.FromResult(0);
        }

        public IRecycleBinService AsRecycleBin() => RecycleBinProxy.Create(this);
    }

    /// <summary>The recycle bin has many members and the pass uses one: a proxy that answers CleanupAsync through the counting service.</summary>
    internal class RecycleBinProxy : System.Reflection.DispatchProxy
    {
        private CountingService? service;

        public static IRecycleBinService Create(CountingService service)
        {
            var proxy = Create<IRecycleBinService, RecycleBinProxy>();
            ((RecycleBinProxy)(object)proxy).service = service;
            return proxy;
        }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IRecycleBinService.CleanupAsync))
            {
                service!.Called();
                return Task.FromResult(0);
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
