using Deluno.Integrations.DownloadClients;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Worker;

/// <summary>
/// The orphaned-downloads refresher, over a real downloads folder: it scans after 6 s and then every 60 s, so a leftover that
/// appears is on the list at the next scan and the Queue list is current without a request ever walking a folder.
/// </summary>
public sealed class TheOrphanedDownloadRefresherRescansEachMinuteTests
{
    private sealed class QuickClock : TimeProvider
    {
        public List<TimeSpan> Delays { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (Delays)
            {
                Delays.Add(dueTime);
            }

            return base.CreateTimer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    private sealed class FailingScopes(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        public bool Fail { get; set; }

        public IServiceScope CreateScope() => Fail ? throw new IOException("The download folder cannot be read.") : inner.CreateScope();
    }

    private sealed class RefresherLog : ILogger<OrphanedDownloadRefreshService>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Levels)
            {
                Levels.Add(logLevel);
            }
        }
    }

    private static string Leftover(string downloads, string name)
    {
        var folder = Directory.CreateDirectory(Path.Combine(downloads, name)).FullName;
        var file = Path.Combine(folder, name + ".mkv");
        File.WriteAllBytes(file, new byte[2048]);
        var old = DateTime.UtcNow.AddDays(-2);
        File.SetLastWriteTimeUtc(file, old);
        Directory.SetLastWriteTimeUtc(folder, old);
        return folder;
    }

    [Fact]
    public async Task A_leftover_that_appears_between_two_scans_is_listed_by_the_next_one_and_the_waits_are_six_seconds_then_a_minute()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var disk = await RealDiskLibrary.OpenAsync(app);
        var downloads = disk.Downloads;
        var movie = await disk.AddMovieAsync("Leftover Film", 2020);
        await disk.RecordDispatchAsync(movie.Id, "First.Leftover.2020.1080p-GRP");
        await disk.RecordDispatchAsync(movie.Id, "Second.Leftover.2021.1080p-GRP");
        var first = Leftover(downloads, "First.Leftover.2020.1080p-GRP");
        var cache = app.Services.GetRequiredService<OrphanedDownloadCache>();
        var clock = new QuickClock();
        var refresher = new OrphanedDownloadRefreshService(cache, clock, NullLogger<OrphanedDownloadRefreshService>.Instance);

        await refresher.StartAsync(CancellationToken.None);
        try
        {
            await Wait.UntilAsync(
                () => cache.Current?.Value.Any(item => string.Equals(item.Path, first, StringComparison.OrdinalIgnoreCase)) == true,
                "the first scan to list the first leftover");
            var second = Leftover(downloads, "Second.Leftover.2021.1080p-GRP");

            await Wait.UntilAsync(
                () => cache.Current?.Value.Any(item => string.Equals(item.Path, second, StringComparison.OrdinalIgnoreCase)) == true,
                "a later scan to list the leftover that appeared after the first");
        }
        finally
        {
            await refresher.StopAsync(CancellationToken.None);
        }

        lock (clock.Delays)
        {
            Assert.Equal(TimeSpan.FromSeconds(6), clock.Delays[0]);
            Assert.All(clock.Delays.Skip(1), delay => Assert.Equal(TimeSpan.FromSeconds(60), delay));
        }
    }

    [Fact]
    public async Task A_scan_that_fails_is_kept_to_debug_and_the_last_list_stays()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var disk = await RealDiskLibrary.OpenAsync(app);
        var movie = await disk.AddMovieAsync("Kept Film", 2020);
        await disk.RecordDispatchAsync(movie.Id, "Kept.Leftover.2020.1080p-GRP");
        var leftover = Leftover(disk.Downloads, "Kept.Leftover.2020.1080p-GRP");
        var scopes = new FailingScopes(app.Services.GetRequiredService<IServiceScopeFactory>());
        var cache = new OrphanedDownloadCache(scopes, TimeProvider.System, NullLogger<OrphanedDownloadCache>.Instance);
        await cache.RefreshAsync(CancellationToken.None);
        var held = Assert.IsAssignableFrom<IReadOnlyList<OrphanedDownloadItem>>(cache.Current!.Value);
        Assert.Contains(held, item => string.Equals(item.Path, leftover, StringComparison.OrdinalIgnoreCase));
        var log = new RefresherLog();
        var refresher = new OrphanedDownloadRefreshService(cache, new QuickClock(), log);
        scopes.Fail = true;

        await refresher.StartAsync(CancellationToken.None);
        try
        {
            await Wait.UntilAsync(
                () =>
                {
                    lock (log.Levels)
                    {
                        return log.Levels.Count >= 2;
                    }
                },
                "the refresher to meet the failing scan twice");
        }
        finally
        {
            await refresher.StopAsync(CancellationToken.None);
        }

        lock (log.Levels)
        {
            Assert.All(log.Levels, level => Assert.Equal(LogLevel.Debug, level));
        }

        Assert.Same(held, cache.Current!.Value);
    }
}
