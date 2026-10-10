using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.DownloadClients;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Platform.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// #1223: strike and health records for downloads that no longer exist in any client stayed listed
/// and counted under Failed. A download that has left its client (removed by Deluno, by a person, or
/// by the client's own clean-up) closes its records; a client that did not answer proves nothing.
/// </summary>
public sealed class ADownloadGoneFromItsClientStopsCountingTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-10T01:00:00Z");
    private const string Client = "client-1";
    private const string Gone = "aaaa0000aaaa0000aaaa0000aaaa0000aaaa0000";
    private const string Other = "bbbb1111bbbb1111bbbb1111bbbb1111bbbb1111";

    [Fact]
    public async Task A_struck_download_that_leaves_its_client_closes_its_records_at_once()
    {
        using var world = await World.CreateAsync();
        await world.RecordAsync(Start, listed: [Gone, Other], held: null, Stalled(Gone), Stalled(Other));
        Assert.Equal(2, (await world.RecordsAsync()).Count);

        // The client answers next time and no longer holds Gone.
        await world.RecordAsync(Start.AddMinutes(1), listed: [Other], held: [Other], Stalled(Other));

        var left = Assert.Single(await world.RecordsAsync());
        Assert.Equal(Other, left.QueueItemId);
    }

    [Fact]
    public async Task A_client_that_answers_with_nothing_closes_every_record_it_had()
    {
        using var world = await World.CreateAsync();
        await world.RecordAsync(Start, listed: [Gone], held: null, Stalled(Gone));

        await world.RecordAsync(Start.AddMinutes(1), listed: [], held: []);

        Assert.Empty(await world.RecordsAsync());
    }

    [Fact]
    public async Task A_client_that_did_not_answer_never_makes_a_download_gone()
    {
        using var world = await World.CreateAsync();
        await world.RecordAsync(Start, listed: [Gone], held: null, Stalled(Gone));

        // held: null is how a briefly unreachable client reaches the repository.
        await world.RecordAsync(Start.AddMinutes(1), listed: [], held: null);

        Assert.Single(await world.RecordsAsync());
    }

    [Fact]
    public async Task A_download_still_in_the_clients_history_is_not_gone()
    {
        using var world = await World.CreateAsync();
        await world.RecordAsync(Start, listed: [Gone], held: null, Stalled(Gone));

        await world.RecordAsync(Start.AddMinutes(1), listed: [], held: [Gone]);

        Assert.Single(await world.RecordsAsync());
    }

    [Fact]
    public async Task Records_already_orphaned_close_on_the_next_pass_and_other_clients_are_left_alone()
    {
        using var world = await World.CreateAsync();
        // Orphans as the rig has them: recorded long ago, never listed again.
        await world.RecordAsync(Start, listed: [Gone], held: null, Stalled(Gone));
        await world.Repository(Start).RecordDownloadHealthObservationsAsync(
            "client-2", ["cccc"], [new DownloadHealthObservation("client-2", "cccc", "Other.Release", DownloadHealthStrikes.ClientStalled, "critical", "Queue status: stalled.")],
            DownloadHealthStrikes.DefaultWindow, TimeSpan.FromHours(72), CancellationToken.None);
        Assert.Equal(2, (await world.RecordsAsync()).Count);

        await world.RecordAsync(Start.AddDays(1), listed: [Other], held: [Other]);

        var left = Assert.Single(await world.RecordsAsync());
        Assert.Equal("client-2", left.ClientId);
    }

    [Theory]
    [InlineData(0, "0 peers")]
    [InlineData(1, "1 peer")]
    [InlineData(2, "2 peers")]
    public void A_count_takes_the_right_plural(int count, string expected)
        => Assert.Equal(expected, PluralWords.Count(count, "peer"));

    [Fact]
    public void A_count_takes_an_irregular_plural_and_minutes_round()
    {
        Assert.Equal("1 entry", PluralWords.Count(1, "entry", "entries"));
        Assert.Equal("3 entries", PluralWords.Count(3, "entry", "entries"));
        Assert.Equal("1 minute", PluralWords.Minutes(0.6));
        Assert.Equal("45 minutes", PluralWords.Minutes(45));
    }

    private static DownloadHealthObservation Stalled(string id)
        => new(Client, id, "Big.Buck.Bunny.2008.1080p", DownloadHealthStrikes.ClientStalled, "critical", "Queue status: stalled.");

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

        public SqliteDownloadHealthRepository Repository(DateTimeOffset at) => new(_storage.Factory, new FixedTimeProvider(at));

        public Task<IReadOnlyList<DownloadHealthRecord>> RecordAsync(
            DateTimeOffset at,
            IReadOnlyCollection<string> listed,
            IReadOnlyCollection<string>? held,
            params DownloadHealthObservation[] findings)
            => Repository(at).RecordDownloadHealthObservationsAsync(
                Client, listed, findings, DownloadHealthStrikes.DefaultWindow, TimeSpan.FromHours(72), CancellationToken.None, held);

        public Task<IReadOnlyList<DownloadHealthRecord>> RecordsAsync()
            => Repository(Start.AddDays(2)).ListDownloadHealthRecordsAsync(50, CancellationToken.None);

        public void Dispose() => _storage.Dispose();
    }
}
