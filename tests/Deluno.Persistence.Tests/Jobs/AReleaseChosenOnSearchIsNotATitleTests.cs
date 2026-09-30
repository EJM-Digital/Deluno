using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// #1062: a release chosen by hand on Search is recorded against a generated id,
/// not a title. Nothing that looks a download up may prefer that title-less
/// record over the dispatch a title already owns, and nothing may search for
/// "another copy" of it.
/// </summary>
public sealed class AReleaseChosenOnSearchIsNotATitleTests
{
    private const string Hash = "355a89defb471e69d65e8cb1936f87e3a5f4302e";

    [Theory]
    [InlineData("movie", true)]
    [InlineData("series", true)]
    [InlineData("episode", true)]
    [InlineData("release", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_a_title_dispatch_has_a_title_to_search_for(string? entityType, bool hasTitle)
    {
        Assert.Equal(hasTitle, DispatchTargets.HasTitle(entityType));
    }

    [Fact]
    public async Task A_titled_dispatch_wins_over_a_newer_title_less_one_for_the_same_hash()
    {
        var (store, storage) = await StoreAsync();
        await InsertAsync(storage, "dispatch-title", "movie", "movie-kid", Hash.ToUpperInvariant(), DateTimeOffset.UtcNow.AddHours(-2));
        await InsertAsync(storage, "dispatch-search", "release", "0a1b2c3d", Hash, DateTimeOffset.UtcNow.AddMinutes(-1));

        var link = await store.FindDispatchLinkByClientItemAsync("client-1", Hash, CancellationToken.None);

        Assert.Equal("movie-kid", link?.EntityId);
    }

    [Fact]
    public async Task A_release_chosen_on_search_is_still_found_when_it_is_the_only_record()
    {
        var (store, storage) = await StoreAsync();
        await InsertAsync(storage, "dispatch-search", "release", "0a1b2c3d", Hash, DateTimeOffset.UtcNow);

        var link = await store.FindDispatchLinkByClientItemAsync("client-1", Hash.ToUpperInvariant(), CancellationToken.None);

        Assert.Equal("release", link?.EntityType);
    }

    [Fact]
    public async Task Recording_a_dispatch_spends_the_reservation_that_stood_in_for_it()
    {
        var (store, storage) = await StoreAsync();
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, TimeProvider.System);
        await dispatches.ReserveGrabAsync("client-1", Hash, "Release", CancellationToken.None);
        Assert.True(await dispatches.HasOtherGrabReservationAsync("client-1", Hash, "someone-else", CancellationToken.None));

        await store.RecordDownloadDispatchAsync(
            "library-1", "movies", "movie", "movie-kid", "Release", "Fixture", "client-1", "qBittorrent", "sent", null,
            cancellationToken: CancellationToken.None, clientExternalId: Hash);

        Assert.False(await dispatches.HasOtherGrabReservationAsync("client-1", Hash, "someone-else", CancellationToken.None));
    }

    [Fact]
    public async Task An_indexer_that_would_not_hand_the_release_over_is_not_blamed_on_the_client_in_Activity()
    {
        var (store, _) = await StoreAsync();
        var failure = Deluno.Contracts.IntegrationFailureFactory.FromLegacy(
            "download-client", "client-1", "client-1", "fetch", "indexer", "Example refused to hand over the release (it answered 403).");

        await store.RecordDownloadDispatchAsync(
            "library-1", "movies", "release", "0a1b2c3d", "Release", "Example", "client-1", "qBittorrent", "unavailable", null,
            grabFailureCode: null, cancellationToken: CancellationToken.None, failure: failure);

        var line = Assert.Single(await store.ListActivityAsync(10, null, null, CancellationToken.None), item => item.Category == "download.dispatch.failed");
        Assert.Contains("from the indexer", line.Message);
        Assert.Contains("refused to hand over", line.Message);
        Assert.DoesNotContain("could not send", line.Message);
    }

    private static async Task<(SqliteJobStore Store, TestStorage Storage)> StoreAsync()
    {
        var storage = TestStorage.Create();
        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, TimeProvider.System),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return (new SqliteJobStore(storage.Factory, TimeProvider.System, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository()), storage);
    }

    private static async Task InsertAsync(TestStorage storage, string id, string entityType, string entityId, string hash, DateTimeOffset created)
    {
        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs, CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO download_dispatches (
                id, library_id, media_type, entity_type, entity_id, release_name,
                indexer_name, download_client_id, download_client_name, status, torrent_hash_or_item_id, created_utc
            ) VALUES (@id, 'library-1', 'movies', @entityType, @entityId, 'Release', 'Fixture', 'client-1', 'qBittorrent', 'sent', @hash, @created)
            """;
        command.Parameters.Add(new SqliteParameter("@id", id));
        command.Parameters.Add(new SqliteParameter("@entityType", entityType));
        command.Parameters.Add(new SqliteParameter("@entityId", entityId));
        command.Parameters.Add(new SqliteParameter("@hash", hash));
        command.Parameters.Add(new SqliteParameter("@created", created.ToString("O")));
        await command.ExecuteNonQueryAsync();
    }
}
