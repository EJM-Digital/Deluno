using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Worker.Dashboard;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Deluno.Infrastructure.Storage;

namespace Deluno.Persistence.Tests.Worker;

/// <summary>
/// The Dashboard's disk reading, taken in the background so no request touches a disk: library, downloads and data folders
/// on real disks are sized and grouped by volume, a folder that cannot be read is still listed as unavailable instead of
/// vanishing, a refresh that throws keeps the last reading, and the refresher reads 2 s after start and then every 60 s.
/// </summary>
public sealed class TheDashboardVolumeReadingSurvivesWhatItCannotReadTests
{
    /// <summary>Records every delay asked of it and lets each one end at once.</summary>
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

    private sealed class ScopesThatFailOnce(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        public bool Fail { get; set; }

        public IServiceScope CreateScope()
            => Fail ? throw new IOException("the libraries could not be read") : inner.CreateScope();
    }

    private sealed class ScopesHeldOpen(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        public ManualResetEventSlim Gate { get; } = new(false);

        public int Opened;

        public IServiceScope CreateScope()
        {
            Interlocked.Increment(ref Opened);
            Gate.Wait(TimeSpan.FromSeconds(30)); // reason: a failed test must not hang; 30 s is far past any real read
            return inner.CreateScope();
        }
    }

    private static DashboardVolumeCache CacheOver(ApplicationTestHost app, IServiceScopeFactory scopes, TimeProvider clock)
        => new(scopes, Options.Create(new StoragePathOptions { DataRoot = app.DataRoot }), clock, NullLogger<DashboardVolumeCache>.Instance);

    private static async Task AddLibraryAsync(ApplicationTestHost app, string root, string? downloads)
        => await app.Services.GetRequiredService<ILibrariesRepository>().CreateLibraryAsync(
            new CreateLibraryRequest(
                Name: "Films", MediaType: "movies", Purpose: "collection", RootPath: root, DownloadsPath: downloads, QualityProfileId: null,
                ImportWorkflow: "standard", ProcessorName: null, ProcessorOutputPath: null, ProcessorTimeoutMinutes: null, ProcessorFailureMode: null,
                AutoSearchEnabled: false, MissingSearchEnabled: false, UpgradeSearchEnabled: false, SearchIntervalMinutes: null,
                RetryDelayHours: null, MaxItemsPerRun: null),
            CancellationToken.None);

    [Fact]
    public async Task Folders_on_real_disks_are_sized_and_the_ones_on_one_volume_are_listed_once_with_every_role()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "films")).FullName;
        var downloads = Directory.CreateDirectory(Path.Combine(app.DataRoot, "incoming")).FullName;
        await AddLibraryAsync(app, root, downloads);
        var cache = CacheOver(app, app.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);

        await cache.RefreshAsync(CancellationToken.None);

        var reading = Assert.IsType<DashboardVolumeReading>(cache.Latest);
        var volume = Assert.Single(reading.Volumes);
        Assert.True(volume.Available);
        Assert.True(volume.TotalBytes > 0);
        Assert.Contains("library", volume.Roles);
        Assert.Contains("downloads", volume.Roles);
        Assert.Contains("data", volume.Roles);
        Assert.Contains(root, volume.Paths);
        Assert.Contains(downloads, volume.Paths);
    }

    [Fact]
    public async Task A_folder_that_cannot_be_read_is_still_listed_by_name_as_unavailable()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "films")).FullName;
        const string unreadable = "not\0a path";
        await AddLibraryAsync(app, root, unreadable);
        var cache = CacheOver(app, app.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);

        await cache.RefreshAsync(CancellationToken.None);

        var missing = Assert.Single(Assert.IsType<DashboardVolumeReading>(cache.Latest).Volumes, volume => !volume.Available);
        Assert.Equal("Films downloads", missing.Label);
        Assert.Equal([unreadable], missing.Paths);
        Assert.Null(missing.TotalBytes);
        Assert.Null(missing.FreeBytes);
    }

    [Fact]
    public async Task Two_refreshes_at_once_share_one_read_and_the_next_one_after_it_reads_again()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scopes = new ScopesHeldOpen(app.Services.GetRequiredService<IServiceScopeFactory>());
        var cache = CacheOver(app, scopes, TimeProvider.System);

        var first = cache.RefreshAsync(CancellationToken.None);
        var second = cache.RefreshAsync(CancellationToken.None);
        Assert.Same(first, second);
        scopes.Gate.Set();
        await Task.WhenAll(first, second);

        Assert.Equal(1, scopes.Opened);
        Assert.NotNull(cache.Latest);
        await cache.RefreshAsync(CancellationToken.None);
        Assert.Equal(2, scopes.Opened);
    }

    [Fact]
    public async Task A_refresh_that_throws_keeps_the_last_reading()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var scopes = new ScopesThatFailOnce(app.Services.GetRequiredService<IServiceScopeFactory>());
        var cache = CacheOver(app, scopes, TimeProvider.System);
        await cache.RefreshAsync(CancellationToken.None);
        var last = Assert.IsType<DashboardVolumeReading>(cache.Latest);

        scopes.Fail = true;
        await cache.RefreshAsync(CancellationToken.None);

        Assert.Same(last, cache.Latest);
    }

    [Fact]
    public async Task The_refresher_waits_two_seconds_after_start_and_then_a_minute_between_readings()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var clock = new QuickClock();
        var cache = CacheOver(app, app.Services.GetRequiredService<IServiceScopeFactory>(), clock);
        var refresher = new DashboardVolumeRefresher(cache, clock);

        await refresher.StartAsync(CancellationToken.None);
        try
        {
            await Wait.UntilAsync(
                () =>
                {
                    lock (clock.Delays)
                    {
                        return clock.Delays.Count >= 3 && cache.Latest is not null;
                    }
                },
                "the refresher to take its first readings");
        }
        finally
        {
            await refresher.StopAsync(CancellationToken.None);
        }

        lock (clock.Delays)
        {
            Assert.Equal(TimeSpan.FromSeconds(2), clock.Delays[0]);
            Assert.All(clock.Delays.Skip(1), delay => Assert.Equal(TimeSpan.FromSeconds(60), delay));
        }
    }
}
