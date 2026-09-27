using Deluno.Contracts.Connectivity;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Platform.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// #901: a strike is time a problem has lasted (#560), and outage time must
/// not be part of that time. Every poll here is within #560's half-hour
/// continuity window of the last, so the spell never resets on its own -
/// only the outage inside it should change the count.
/// </summary>
public sealed class AnOutageFreezesTheStrikeClockTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-27T14:00:00Z");
    private const string Client = "client-1";
    private const string Torrent = "f0e550cbffa15a11db1db04b80c984de6d668268";
    private const string Release = "Big.Buck.Bunny.2008.1080p.WEB-DL.DDP5.1.H.264-DELUNO";

    [Fact]
    public async Task With_no_outage_the_clock_runs_exactly_as_560_left_it()
    {
        using var world = await World.CreateAsync();

        var strikes = new List<int>();
        foreach (var minute in new[] { 0, 30, 60, 90 })
        {
            strikes.Add(Assert.Single(await world.SeeAsync(Start.AddMinutes(minute), Stalled())).StrikeCount);
        }

        Assert.Equal([1, 2, 3, 4], strikes);
    }

    [Fact]
    public async Task Strikes_do_not_grow_while_the_outage_the_whole_spell_sits_in_is_still_open()
    {
        using var world = await World.CreateAsync();
        // Outage opens exactly when the stall was first seen and has not
        // closed: every check from here on falls entirely inside it, so the
        // clock should never move past strike 1.
        world.Connectivity.Windows.Add(new ConnectivityOutageWindow(Start, null));

        var strikes = new List<int>();
        foreach (var minute in new[] { 0, 30, 60, 90 })
        {
            strikes.Add(Assert.Single(await world.SeeAsync(Start.AddMinutes(minute), Stalled())).StrikeCount);
        }

        Assert.All(strikes, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task A_half_hour_outage_in_the_middle_of_the_spell_costs_it_exactly_one_strike()
    {
        using var world = await World.CreateAsync();
        // Down for the 30 minutes from t=20 to t=50.
        world.Connectivity.Windows.Add(new ConnectivityOutageWindow(Start.AddMinutes(20), Start.AddMinutes(50)));

        var strikes = new List<int>();
        foreach (var minute in new[] { 0, 15, 30, 45, 60, 75, 90 })
        {
            strikes.Add(Assert.Single(await world.SeeAsync(Start.AddMinutes(minute), Stalled())).StrikeCount);
        }

        // Without the outage this would be [1, 1, 2, 2, 3, 3, 4] (#560's own
        // arithmetic for 15-minute polling). The 30 minutes of outage time
        // are never counted, so the spell finishes exactly one strike behind
        // where it would otherwise be.
        Assert.Equal([1, 1, 1, 1, 2, 2, 3], strikes);
    }

    [Fact]
    public async Task An_outage_that_has_already_closed_is_still_subtracted()
    {
        using var world = await World.CreateAsync();

        var first = Assert.Single(await world.SeeAsync(Start, Stalled()));
        Assert.Equal(1, first.StrikeCount);

        // The outage happens, and closes, entirely between two polls.
        world.Connectivity.Windows.Add(new ConnectivityOutageWindow(Start.AddMinutes(5), Start.AddMinutes(25)));
        var second = Assert.Single(await world.SeeAsync(Start.AddMinutes(30), Stalled()));

        // 30 real minutes elapsed, 20 of them outage: 10 counted minutes is
        // still short of the next strike.
        Assert.Equal(1, second.StrikeCount);
    }

    private static DownloadHealthObservation Stalled()
        => new(Client, Torrent, Release, DownloadHealthStrikes.ClientStalled, "critical", "Queue status: stalled.");

    private sealed class FakeConnectivityMonitor : IConnectivityMonitor
    {
        public List<ConnectivityOutageWindow> Windows { get; } = [];

        public Task<ConnectivityStatus> GetStatusAsync(CancellationToken cancellationToken)
            => Task.FromResult(Current);

        public ConnectivityStatus Current => new(false, null, DateTimeOffset.MinValue, [], "Not checked yet.");

        public Task<TimeSpan> OutageOverlapAsync(DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken)
        {
            if (until <= since) return Task.FromResult(TimeSpan.Zero);

            var overlap = TimeSpan.Zero;
            foreach (var window in Windows)
            {
                var start = window.StartUtc > since ? window.StartUtc : since;
                var windowEnd = window.EndUtc ?? until;
                var end = windowEnd < until ? windowEnd : until;
                if (end > start) overlap += end - start;
            }

            return Task.FromResult(overlap);
        }
    }

    private sealed class World : IDisposable
    {
        private readonly TestStorage _storage;

        public FakeConnectivityMonitor Connectivity { get; } = new();

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

        public SqliteDownloadHealthRepository Repository(DateTimeOffset at) => new(_storage.Factory, new FixedTimeProvider(at), Connectivity);

        public Task<IReadOnlyList<DownloadHealthRecord>> SeeAsync(DateTimeOffset at, params DownloadHealthObservation[] findings)
            => Repository(at).RecordDownloadHealthObservationsAsync(Client, [Torrent], findings, CancellationToken.None);

        public void Dispose() => _storage.Dispose();
    }
}
