using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// #617, live on the rig: the grab kept LimeTorrents' name "The Kid 1921 CC
/// 1080p BluRay x264 nikt0"; qBittorrent's torrent is "…x264-nikt0". Found by
/// exact name, the grab was missed and the film imported as a new title named
/// after its file.
/// </summary>
public sealed class TheGrabBehindADownloadIsFoundTests
{
    private const string Indexed = "The Kid 1921 CC 1080p BluRay x264 nikt0";
    private const string InTheClient = "The Kid 1921 CC 1080p BluRay x264-nikt0";
    private const string Hash = "355a89defb471e69d65e8cb1936f87e3a5f4302e";

    [Fact]
    public async Task By_the_torrents_hash_whatever_its_name()
    {
        var store = await StoreWithGrabAsync();

        var link = await store.FindDispatchLinkForDownloadAsync("qbittorrent-main", Hash.ToUpperInvariant(), "something else entirely", CancellationToken.None);

        Assert.Equal("movie-kid", link?.EntityId);
    }

    [Fact]
    public async Task By_its_name_whatever_the_punctuation_and_case()
    {
        var store = await StoreWithGrabAsync();

        Assert.Equal("movie-kid", (await store.FindRecentDispatchLinkAsync("qbittorrent-main", InTheClient, CancellationToken.None))?.EntityId);
        Assert.Equal("movie-kid", (await store.FindRecentDispatchLinkAsync("qbittorrent-main", "the.kid.1921.cc.1080p.bluray.x264.nikt0", CancellationToken.None))?.EntityId);
    }

    /// <summary>A tracker's display title for the torrent, and the torrent's own name, as seen with Deluge.</summary>
    [Fact]
    public async Task By_the_title_and_year_each_name_gives_when_nothing_else_ties_them()
    {
        var store = await StoreWithGrabAsync("House on Haunted Hill (1959) [P&M] 1080p Blu-Ray (6.6GB)", hash: null);

        Assert.Equal("movie-kid", (await store.FindRecentDispatchLinkAsync("qbittorrent-main", "house.on.-haunted.-hill.-1959.1080p.-blu-ray.-x-264-amiable", CancellationToken.None))?.EntityId);
        Assert.Null(await store.FindRecentDispatchLinkAsync("qbittorrent-main", "House.on.Haunted.Hill.1999.1080p.BluRay", CancellationToken.None));
    }

    [Fact]
    public async Task Not_by_another_release_or_another_client()
    {
        var store = await StoreWithGrabAsync();

        // Another film altogether. (Another copy of the same film is the same title and year, and is found.)
        Assert.Null(await store.FindRecentDispatchLinkAsync("qbittorrent-main", "The Kid Detective 2020 1080p WEB-DL", CancellationToken.None));
        Assert.Null(await store.FindRecentDispatchLinkAsync("deluge-main", InTheClient, CancellationToken.None));
        Assert.Null(await store.FindDispatchLinkByClientItemAsync("deluge-main", Hash, CancellationToken.None));
    }

    private static async Task<SqliteJobStore> StoreWithGrabAsync(string releaseName = Indexed, string? hash = Hash)
    {
        var storage = TestStorage.Create();
        var timeProvider = TimeProvider.System;
        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs, CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO download_dispatches (
                id, library_id, media_type, entity_type, entity_id, release_name,
                indexer_name, download_client_id, download_client_name, status, torrent_hash_or_item_id, created_utc
            ) VALUES (
                'dispatch-kid', 'movies-main', 'movie', 'movie', 'movie-kid', @releaseName,
                'LimeTorrents', 'qbittorrent-main', 'qBittorrent', 'sent', @hash, @createdUtc
            )
            """;
        command.Parameters.Add(new SqliteParameter("@releaseName", releaseName));
        command.Parameters.Add(new SqliteParameter("@hash", (object?)hash ?? DBNull.Value));
        command.Parameters.Add(new SqliteParameter("@createdUtc", DateTimeOffset.UtcNow.AddHours(-2).ToString("O")));
        await command.ExecuteNonQueryAsync();

        return new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
    }
}
