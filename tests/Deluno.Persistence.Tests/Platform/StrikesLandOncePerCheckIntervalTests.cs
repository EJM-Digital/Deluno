using Deluno.Contracts.Connectivity;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Platform.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// #1198: a strike lands once per "Check every" interval, not at the interval minus 30 seconds, and the record never
/// counts past the rule's own limit, so 3 strikes means removal at strike 3. Driven with a fake clock: the telemetry
/// polls every ten seconds, far more often than the interval.
/// </summary>
public sealed class StrikesLandOncePerCheckIntervalTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-10T08:00:00Z");
    private const string Client = "client-1";
    private const string Torrent = "f0e550cbffa15a11db1db04b80c984de6d668268";

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public async Task A_strike_lands_exactly_one_interval_after_the_last_however_often_it_is_polled(int minutes)
    {
        using var world = await World.CreateAsync();
        var interval = TimeSpan.FromMinutes(minutes);
        var landed = new List<DateTimeOffset>();
        var previous = 0;

        for (var second = 0; second <= minutes * 60 * 5; second += 10)
        {
            var record = Assert.Single(await world.SeeAsync(Start.AddSeconds(second), interval, Observation(strikeLimit: 50)));
            if (record.StrikeCount != previous)
            {
                landed.Add(record.LastStrikeUtc!.Value);
                previous = record.StrikeCount;
            }
        }

        Assert.Equal(6, landed.Count);
        for (var index = 1; index < landed.Count; index++)
        {
            Assert.Equal(interval, landed[index] - landed[index - 1]);
        }
    }

    [Fact]
    public async Task No_strike_lands_before_a_whole_interval_has_passed()
    {
        using var world = await World.CreateAsync();
        var interval = TimeSpan.FromMinutes(1);

        await world.SeeAsync(Start, interval, Observation(strikeLimit: 50));
        var early = Assert.Single(await world.SeeAsync(Start.AddSeconds(59), interval, Observation(strikeLimit: 50)));
        var onTime = Assert.Single(await world.SeeAsync(Start.AddSeconds(60), interval, Observation(strikeLimit: 50)));

        Assert.Equal(1, early.StrikeCount);
        Assert.Equal(2, onTime.StrikeCount);
    }

    [Fact]
    public async Task A_limit_of_three_reaches_three_at_two_intervals_and_never_reads_four_of_three()
    {
        using var world = await World.CreateAsync();
        var interval = TimeSpan.FromMinutes(1);
        var counts = new List<int>();

        for (var second = 0; second <= 300; second += 10)
        {
            counts.Add(Assert.Single(await world.SeeAsync(Start.AddSeconds(second), interval, Observation(strikeLimit: 3))).StrikeCount);
        }

        Assert.Equal(1, counts[0]);
        Assert.Equal(3, counts[12]); // 120 s: strike 3 lands at exactly two intervals after strike 1
        Assert.Equal(2, counts[11]);
        Assert.All(counts, count => Assert.InRange(count, 1, 3));
    }

    [Fact]
    public void The_scheduled_moment_is_kept_for_a_late_pass_and_restarts_after_a_long_gap()
    {
        var interval = TimeSpan.FromMinutes(1);
        Assert.Equal(Start.AddSeconds(60), DownloadHealthStrikes.StrikeLandedAt(Start, Start.AddSeconds(67), interval, TimeSpan.Zero));
        Assert.Equal(Start.AddMinutes(10), DownloadHealthStrikes.StrikeLandedAt(Start, Start.AddMinutes(10), interval, TimeSpan.Zero));
        Assert.False(DownloadHealthStrikes.IsDueForStrike(Start, Start.AddSeconds(59), interval, TimeSpan.Zero));
        Assert.True(DownloadHealthStrikes.IsDueForStrike(Start, Start.AddSeconds(60), interval, TimeSpan.Zero));
    }

    private static DownloadHealthObservation Observation(int strikeLimit)
        => new(Client, Torrent, "Big.Buck.Bunny.2008.1080p.WEB-DL.DDP5.1.H.264-DELUNO", DownloadHealthStrikes.NoThroughput, "warning",
            "Nothing arrived.", "rule-no-progress", strikeLimit, 1, "replace");

    private sealed class NoOutage : IConnectivityMonitor
    {
        public Task<ConnectivityStatus> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(Current);
        public ConnectivityStatus Current => new(false, null, DateTimeOffset.MinValue, [], "Not checked yet.");
        public Task<TimeSpan> OutageOverlapAsync(DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken) => Task.FromResult(TimeSpan.Zero);
    }

    private sealed class World : IDisposable
    {
        private readonly TestStorage _storage;
        private World(TestStorage storage) => _storage = storage;

        public static async Task<World> CreateAsync()
        {
            var storage = TestStorage.Create();
            await new PlatformSchemaInitializer(
                storage.Factory,
                new SqliteDatabaseMigrator(storage.Factory, new FixedTimeProvider(Start)),
                NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            return new World(storage);
        }

        public Task<IReadOnlyList<DownloadHealthRecord>> SeeAsync(DateTimeOffset at, TimeSpan interval, params DownloadHealthObservation[] findings)
            => new SqliteDownloadHealthRepository(_storage.Factory, new FixedTimeProvider(at), new NoOutage())
                .RecordDownloadHealthObservationsAsync(Client, [Torrent], findings, interval, TimeSpan.FromHours(72), CancellationToken.None);

        public void Dispose() => _storage.Dispose();
    }
}
