using Deluno.Integrations.DownloadClients;
using Deluno.Persistence.Tests.Support;
using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Deluno.Persistence.Tests.BackgroundLoops;

/// <summary>
/// Coverage register (#1329), background workers inventory: the download-folder notice service waits 20 s after the start before
/// its one pass, and an exception in that pass is caught and logged, writing no marker, so the next start tries again.
/// </summary>
public sealed class TheDownloadFolderNoticeWaitsTwentySecondsAndRetriesOnFailureTests
{
    private sealed class CountingClock(DateTimeOffset start) : FakeTimeProvider(start)
    {
        public int Timers;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref Timers);
            return timer;
        }
    }

    private sealed class CountingScopes(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        public int Created;

        public IServiceScope CreateScope()
        {
            Interlocked.Increment(ref Created);
            return inner.CreateScope();
        }
    }

    private sealed class ListLogger : ILogger<DownloadFolderChangeNoticeService>
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines) { Lines.Add((logLevel, formatter(state, exception))); }
        }
    }

    [Fact]
    public async Task Nothing_runs_for_twenty_seconds_then_a_pass_that_throws_is_warned_leaves_no_marker_and_does_not_end_the_service()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "deluno-notice-" + Guid.NewGuid().ToString("n"));
        var clock = new CountingClock(DateTimeOffset.Parse("2026-10-01T06:00:00Z"));
        // An empty container: the pass asks for the libraries repository and cannot be given one, which is the failure.
        var scopes = new CountingScopes(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
        var logger = new ListLogger();
        var service = new DownloadFolderChangeNoticeService(scopes, Options.Create(new StoragePathOptions { DataRoot = dataRoot }), logger, clock);
        try
        {
            await service.StartAsync(CancellationToken.None);
            await Wait.UntilAsync(() => Volatile.Read(ref clock.Timers) >= 1, "the service's 20 s wait being set on the clock");
            clock.Advance(TimeSpan.FromSeconds(19));
            await Task.Delay(50); // reason: nothing can be awaited for something that must not happen; the fake clock is at 19 s and the service has not been given the 20th
            Assert.Equal(0, scopes.Created);

            clock.Advance(TimeSpan.FromSeconds(1));
            await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1, scopes.Created);
            Assert.Single(logger.Lines, line => line.Level == LogLevel.Warning && line.Message.Contains("Could not work out whether the download folder change needs a note.", StringComparison.Ordinal));
            Assert.False(Directory.Exists(Path.Combine(dataRoot, "upgrade-notices")));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}
