using System.Reflection;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Worker.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Deluno.Persistence.Tests.Worker;

/// <summary>
/// A busy database at start-up must not stop the worker: when it cannot release the leases its previous run left, it
/// says so in the log, writes its heartbeat, starts every lane, and leaves those leases to expire on their own.
/// </summary>
public sealed class TheHeartbeatWorkerCarriesOnWhenReleasingLeasesFailsTests
{
    private static readonly string WorkerId = $"worker-{Environment.MachineName.ToLowerInvariant()}";

    /// <summary>The real job queue, except that releasing leases fails the way a locked database would.</summary>
    public class ReleaseFails : DispatchProxy
    {
        public IJobQueueRepository Inner { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IJobQueueRepository.ReleaseLeasesAsync))
            {
                return Task.FromException<IReadOnlyList<JobQueueItem>>(new InvalidOperationException("database is locked"));
            }

            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                throw exception.InnerException;
            }
        }
    }

    private sealed class SwappingProvider(IServiceProvider inner) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            var real = inner.GetService(serviceType);
            if (serviceType != typeof(IJobQueueRepository) || real is null)
            {
                return real;
            }

            var proxy = DispatchProxy.Create<IJobQueueRepository, ReleaseFails>();
            ((ReleaseFails)(object)proxy).Inner = (IJobQueueRepository)real;
            return proxy;
        }
    }

    private sealed class SwappingScope(IServiceScope inner) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = new SwappingProvider(inner.ServiceProvider);

        public void Dispose() => inner.Dispose();
    }

    private sealed class SwappingScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new SwappingScope(inner.CreateScope());
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines)
            {
                Lines.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    [Fact]
    public async Task The_worker_warns_starts_every_lane_and_leaves_the_previous_runs_lease_to_expire()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var lane = JobLanes.All.First(candidate => candidate.Enabled);
        var left = await app.Services.GetRequiredService<IJobScheduler>().EnqueueAsync(
            new EnqueueJobRequest("filesystem.import.execute", "test", "{\"left\":true}", null, null), CancellationToken.None);
        var until = DateTimeOffset.UtcNow.AddMinutes(10).ToString("O");
        await ApiScenes.ExecuteAsync(app, "jobs",
            $"UPDATE job_queue SET status = 'running', started_utc = '{DateTimeOffset.UtcNow:O}', leased_until_utc = '{until}', worker_id = '{WorkerId}-{lane.Name}' WHERE id = '{left.Id}';");
        var board = new JobLaneStateBoard(TimeProvider.System);
        var logger = new CapturingLogger<DelunoHeartbeatWorker>();
        var worker = new DelunoHeartbeatWorker(
            logger,
            new SwappingScopeFactory(app.Services.GetRequiredService<IServiceScopeFactory>()),
            TimeProvider.System,
            app.Services.GetRequiredService<IJobLaneSignal>(),
            board);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await Wait.UntilAsync(
                async () => await ApiScenes.ScalarAsync(app, "jobs", $"SELECT COUNT(*) FROM worker_heartbeats WHERE worker_id = '{WorkerId}';") == 1
                    && JobLanes.All.Where(candidate => candidate.Enabled).All(candidate => board.Snapshot().Any(reading => reading.Name == candidate.Name)),
                "the worker to write its heartbeat and start its lanes",
                TimeSpan.FromSeconds(30)); // reason: a background job on a busy runner takes many times its usual few seconds; 30 s only stops a hang

            Assert.Equal("running", await ApiScenes.TextAsync(app, "jobs", $"SELECT status FROM job_queue WHERE id = '{left.Id}';"));
            lock (logger.Lines)
            {
                Assert.Contains(logger.Lines, line =>
                    line.Level == LogLevel.Warning && line.Message.Contains("could not release leases held by its previous incarnation"));
            }
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }
}
