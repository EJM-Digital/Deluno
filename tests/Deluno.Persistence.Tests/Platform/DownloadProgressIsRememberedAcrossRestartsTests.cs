using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Platform.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// #659: the no-progress clock a stall check reads has to survive a process
/// restart, or every restart looks like a fresh gain and a genuinely dead
/// torrent never reaches the stall window. Each call below builds a brand new
/// <see cref="SqliteDownloadHealthRepository"/> against the same database, the
/// same way <c>AStrikeIsAProblemThatLastsTests.World.Repository</c> already
/// simulates one poll per process for the finding-based strike clock: no
/// state survives in the instance itself, only in the database underneath it.
/// </summary>
public sealed class DownloadProgressIsRememberedAcrossRestartsTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-25T07:05:00Z");
    private const string Client = "transmission-1";
    private const string QueueItemId = "abc123";

    [Fact]
    public async Task A_first_sighting_is_not_treated_as_already_stalled()
    {
        using var world = await World.CreateAsync();

        var lastIncrease = await world.SeeAsync(Start, downloadedBytes: 0);

        Assert.Equal(Start, lastIncrease);
    }

    [Fact]
    public async Task No_gain_between_polls_leaves_the_clock_exactly_where_it_was()
    {
        using var world = await World.CreateAsync();
        await world.SeeAsync(Start, downloadedBytes: 716_145);

        var lastIncrease = await world.SeeAsync(Start.AddMinutes(14), downloadedBytes: 716_145);

        Assert.Equal(Start, lastIncrease);
    }

    [Fact]
    public async Task Any_gain_however_small_moves_the_clock_to_now()
    {
        using var world = await World.CreateAsync();
        await world.SeeAsync(Start, downloadedBytes: 716_145);

        var lastIncrease = await world.SeeAsync(Start.AddMinutes(14), downloadedBytes: 720_000);

        Assert.Equal(Start.AddMinutes(14), lastIncrease);
    }

    [Fact]
    public async Task A_slow_but_steady_gain_every_poll_never_lets_the_clock_fall_behind()
    {
        using var world = await World.CreateAsync();
        var at = Start;
        var bytes = 0L;

        for (var poll = 0; poll < 20; poll++)
        {
            at = at.AddMinutes(10);
            bytes += 1_000; // trickling in, but always moving
            await world.SeeAsync(at, bytes);
        }

        var lastIncrease = await world.SeeAsync(at.AddMinutes(10), bytes + 1);

        Assert.Equal(at.AddMinutes(10), lastIncrease);
    }

    [Fact]
    public async Task The_clock_a_restart_would_wipe_from_memory_survives_it_here()
    {
        using var world = await World.CreateAsync();

        // "Restart": the process's poll at t0 never gains again after this.
        await world.SeeAsync(Start, downloadedBytes: 716_145);

        // Every later poll below is its own fresh repository instance -
        // nothing but the database ties them together, standing in for the
        // process having restarted between each one.
        var afterTenMinutes = await world.SeeAsync(Start.AddMinutes(10), downloadedBytes: 716_145);
        var afterTwentyMinutes = await world.SeeAsync(Start.AddMinutes(20), downloadedBytes: 716_145);
        var afterThirtyMinutes = await world.SeeAsync(Start.AddMinutes(31), downloadedBytes: 716_145);

        Assert.Equal(Start, afterTenMinutes);
        Assert.Equal(Start, afterTwentyMinutes);
        Assert.Equal(Start, afterThirtyMinutes);

        // 31 minutes without a gain is past DownloadHealthStrikes.Window (30
        // minutes): a caller reading this clock now has what it needs to call
        // the item stalled, and it only has it because the database - not an
        // in-memory dictionary - carried it across every one of those "restarts".
        Assert.True(Start.AddMinutes(31) - afterThirtyMinutes >= TimeSpan.FromMinutes(30));
    }

    // #951: "reset strikes on progress" - a rule can turn resetting off
    // entirely, or require a minimum gain for a gain to count as "real"
    // progress. Both default to the original "any gain always resets"
    // behaviour, exercised above.
    [Fact]
    public async Task Reset_disabled_never_moves_the_clock_again_once_a_prior_sighting_exists()
    {
        using var world = await World.CreateAsync();
        await world.SeeAsync(Start, downloadedBytes: 716_145, allowReset: false);

        var lastIncrease = await world.SeeAsync(Start.AddMinutes(14), downloadedBytes: 900_000, allowReset: false);

        Assert.Equal(Start, lastIncrease);
    }

    [Fact]
    public async Task A_gain_below_the_configured_minimum_does_not_reset_the_clock()
    {
        using var world = await World.CreateAsync();
        await world.SeeAsync(Start, downloadedBytes: 716_145, minimumGainBytes: 1_000_000);

        // A trickle of 1,000 bytes - well under the configured 1 MB minimum.
        var lastIncrease = await world.SeeAsync(Start.AddMinutes(14), downloadedBytes: 717_145, minimumGainBytes: 1_000_000);

        Assert.Equal(Start, lastIncrease);
    }

    [Fact]
    public async Task A_gain_at_or_above_the_configured_minimum_still_resets_the_clock()
    {
        using var world = await World.CreateAsync();
        await world.SeeAsync(Start, downloadedBytes: 716_145, minimumGainBytes: 1_000_000);

        var lastIncrease = await world.SeeAsync(Start.AddMinutes(14), downloadedBytes: 716_145 + 1_000_000, minimumGainBytes: 1_000_000);

        Assert.Equal(Start.AddMinutes(14), lastIncrease);
    }

    private sealed class World : IDisposable
    {
        private readonly TestStorage _storage;

        private World(TestStorage storage) => _storage = storage;

        public static async Task<World> CreateAsync()
        {
            var storage = TestStorage.Create();
            var time = new FixedTimeProvider(Start);
            await new PlatformSchemaInitializer(
                storage.Factory,
                new SqliteDatabaseMigrator(storage.Factory, time),
                NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            return new World(storage);
        }

        /// <summary>One poll, its own repository instance, standing in for one process lifetime.</summary>
        public async Task<DateTimeOffset> SeeAsync(DateTimeOffset at, long downloadedBytes, bool allowReset = true, long minimumGainBytes = 0)
        {
            var repository = new SqliteDownloadHealthRepository(_storage.Factory, new FixedTimeProvider(at));
            var result = await repository.RecordDownloadProgressAsync(
                Client,
                [(QueueItemId, downloadedBytes)],
                CancellationToken.None,
                allowReset,
                minimumGainBytes);
            return result[QueueItemId];
        }

        public void Dispose() => _storage.Dispose();
    }
}
