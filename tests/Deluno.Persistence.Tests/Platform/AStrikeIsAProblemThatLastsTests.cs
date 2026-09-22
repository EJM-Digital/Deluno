using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Platform.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// #560: a strike is time a download has stayed stopped, not a sighting.
///
/// <para>On the rig, one torrent was fetched three times over two days. Each
/// time qBittorrent called it stalled for the seconds before peers connected,
/// and each download finished and imported. The first rule counted the three
/// sightings as three strikes and blocked the release for 90 days, while a
/// download that stayed stalled and kept being polled never got past one.</para>
/// </summary>
public sealed class AStrikeIsAProblemThatLastsTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-22T01:00:00Z");
    private const string Client = "client-1";
    private const string Torrent = "f0e550cbffa15a11db1db04b80c984de6d668268";
    private const string Release = "Big.Buck.Bunny.2008.1080p.WEB-DL.DDP5.1.H.264-DELUNO";

    [Fact]
    public async Task A_download_stalled_without_a_break_gains_a_strike_each_half_hour()
    {
        using var world = await World.CreateAsync();

        var strikes = new List<int>();
        for (var minute = 0; minute <= 60; minute += 10)
        {
            strikes.Add(Assert.Single(await world.SeeAsync(Start.AddMinutes(minute), Stalled())).StrikeCount);
        }

        Assert.Equal([1, 1, 1, 2, 2, 2, 3], strikes);
    }

    [Fact]
    public async Task Brief_stalls_at_the_start_of_separate_downloads_never_add_up()
    {
        using var world = await World.CreateAsync();

        foreach (var fetched in new[] { Start, Start.AddHours(23), Start.AddHours(24) })
        {
            Assert.Equal(1, Assert.Single(await world.SeeAsync(fetched, Stalled())).StrikeCount);
            await world.SeeAsync(fetched.AddSeconds(30)); // peers found, downloading
        }

        Assert.Empty(await world.RecordsAsync());
    }

    [Fact]
    public async Task A_stall_that_clears_is_over()
    {
        using var world = await World.CreateAsync();
        await world.SeeAsync(Start, Stalled());
        await world.SeeAsync(Start.AddMinutes(20), Stalled());
        await world.SeeAsync(Start.AddMinutes(40), Stalled());

        await world.SeeAsync(Start.AddMinutes(41));

        Assert.Empty(await world.RecordsAsync());
        Assert.Equal(1, Assert.Single(await world.SeeAsync(Start.AddMinutes(42), Stalled())).StrikeCount);
    }

    [Fact]
    public async Task A_download_the_client_did_not_list_keeps_its_record()
    {
        using var world = await World.CreateAsync();
        await world.SeeAsync(Start, Stalled());

        // The client did not answer, or the download left it: nothing is known.
        await world.SeeAsync(Start.AddMinutes(10), listed: []);

        Assert.Single(await world.RecordsAsync());
    }

    [Fact]
    public async Task Seen_again_after_Deluno_stopped_watching_it_starts_again()
    {
        using var world = await World.CreateAsync();
        await world.SeeAsync(Start, Stalled());
        await world.SeeAsync(Start.AddMinutes(20), Stalled());

        var seen = Assert.Single(await world.SeeAsync(Start.AddHours(3), Stalled()));

        Assert.Equal(1, seen.StrikeCount);
        Assert.Equal(Start.AddHours(3), seen.FirstObservedUtc);
    }

    [Fact]
    public async Task One_client_answering_changes_nothing_about_another()
    {
        using var world = await World.CreateAsync();
        await world.SeeAsync(Start, Stalled());

        await world.Repository(Start.AddMinutes(5)).RecordDownloadHealthObservationsAsync("client-2", [Torrent], [], CancellationToken.None);

        Assert.Single(await world.RecordsAsync());
    }

    [Fact]
    public async Task An_ignored_stall_keeps_counting_and_blocks_nothing_until_the_time_is_up()
    {
        using var world = await World.CreateAsync();
        for (var minute = 0; minute <= 60; minute += 10)
        {
            await world.SeeAsync(Start.AddMinutes(minute), Stalled());
        }

        var ignored = await world.Repository(Start.AddMinutes(61)).IgnoreDownloadHealthFindingAsync(Client, Torrent, DownloadHealthStrikes.ClientStalled, 7, CancellationToken.None);
        var seen = Assert.Single(await world.SeeAsync(Start.AddMinutes(70), Stalled()));

        Assert.NotNull(ignored);
        Assert.Equal(3, seen.StrikeCount);
        Assert.False(seen.BlocksCandidate(Start.AddMinutes(70)));
        Assert.True(seen.BlocksCandidate(Start.AddDays(8)));
    }

    [Theory]
    [InlineData(DownloadHealthStrikes.ClientStalled, true)]
    [InlineData(DownloadHealthStrikes.NoThroughput, true)]
    [InlineData("import-failed", false)]
    [InlineData("post-processing-failed", false)]
    [InlineData("excessive-eta", false)]
    [InlineData("missing-import-path", false)]
    public void Only_a_download_that_has_stopped_is_struck(string kind, bool blocks)
    {
        // A failed import or processing run has its own rules about refusing
        // the release (DESIGN-007); a long estimate is a download still moving.
        var record = new DownloadHealthRecord(Client, Torrent, Release, "KEY", kind, "critical", "evidence", Start, Start.AddHours(2), 5, null);

        Assert.Equal(blocks, record.BlocksCandidate(Start.AddHours(2)));
    }

    [Fact]
    public async Task Records_counted_the_old_way_are_removed_rather_than_trusted()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText =
                """
                CREATE TABLE system_settings (setting_key TEXT PRIMARY KEY, setting_value TEXT NOT NULL, updated_utc TEXT NOT NULL);
                INSERT INTO system_settings VALUES ('download-health.records.v1', '[{"StrikeCount":3}]', '2026-09-20T01:17:24Z');
                INSERT INTO system_settings VALUES ('cleanup.strikeThreshold', '3', '2026-09-15T00:00:00Z');
                """;
            await create.ExecuteNonQueryAsync();
        }

        await using (var transaction = connection.BeginTransaction())
        {
            await new V0066DownloadHealthStrikesV2().UpAsync(connection, transaction, CancellationToken.None);
            await transaction.CommitAsync();
        }

        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT group_concat(setting_key) FROM system_settings;";
        Assert.Equal("cleanup.strikeThreshold", await read.ExecuteScalarAsync());
    }

    private static DownloadHealthObservation Stalled()
        => new(Client, Torrent, Release, DownloadHealthStrikes.ClientStalled, "critical", "Queue status: stalled.");

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

        public SqliteDownloadHealthRepository Repository(DateTimeOffset at) => new(_storage.Factory, new FixedTimeProvider(at));

        /// <summary>One snapshot of the client listing the torrent, with these findings.</summary>
        public Task<IReadOnlyList<DownloadHealthRecord>> SeeAsync(DateTimeOffset at, params DownloadHealthObservation[] findings)
            => Repository(at).RecordDownloadHealthObservationsAsync(Client, [Torrent], findings, CancellationToken.None);

        /// <summary>One snapshot listing exactly these downloads, none with a finding.</summary>
        public Task<IReadOnlyList<DownloadHealthRecord>> SeeAsync(DateTimeOffset at, IReadOnlyCollection<string> listed)
            => Repository(at).RecordDownloadHealthObservationsAsync(Client, listed, [], CancellationToken.None);

        public Task<IReadOnlyList<DownloadHealthRecord>> RecordsAsync()
            => Repository(Start.AddDays(2)).ListDownloadHealthRecordsAsync(50, CancellationToken.None);

        public void Dispose() => _storage.Dispose();
    }
}
