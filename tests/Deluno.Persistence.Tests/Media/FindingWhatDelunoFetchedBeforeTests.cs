using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Data;
using Deluno.Media;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Media;

/// <summary>
/// Finding the download Deluno finished, and no longer has anything to show for.
///
/// <para><see cref="AcquisitionBlockerSources.FindAsync"/> deliberately ignores
/// dispatches that finished importing, on the grounds that a download which
/// imported is history rather than an obstacle. That is right for everything it
/// reports, and wrong for exactly one case — the one this whole feature was
/// built for. The file has gone, so that completed download is precisely why
/// the client will refuse the next attempt.</para>
///
/// <para>Read from Deluno's own record rather than by asking a client, so it
/// answers with the client switched off, and so it cannot be wrong about
/// somebody else's state.</para>
/// </summary>
public sealed class FindingWhatDelunoFetchedBeforeTests
{
    [Fact]
    public async Task It_finds_the_download_that_completed()
    {
        using var storage = await StorageAsync();
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, Clock);

        await InsertAsync(storage.Factory, "dispatch-done", "movie-1", "Arrival.2016.1080p", importStatus: "completed");

        var found = await AcquisitionBlockerSources.FindPreviousFetchAsync(
            dispatches, "movies", "movie-1", CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal("qBittorrent", found!.ClientName);
        Assert.Equal("Arrival.2016.1080p", found.ReleaseName);
    }

    /// <summary>
    /// A download still in flight is not something Deluno fetched before — it
    /// is something it is fetching, and the in-flight blocker already says so.
    /// </summary>
    [Fact]
    public async Task It_ignores_a_download_that_has_not_finished()
    {
        using var storage = await StorageAsync();
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, Clock);

        await InsertAsync(storage.Factory, "dispatch-live", "movie-1", "Arrival.2016.1080p", importStatus: null);

        Assert.Null(await AcquisitionBlockerSources.FindPreviousFetchAsync(
            dispatches, "movies", "movie-1", CancellationToken.None));
    }

    [Fact]
    public async Task A_title_never_fetched_finds_nothing()
    {
        using var storage = await StorageAsync();
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, Clock);

        Assert.Null(await AcquisitionBlockerSources.FindPreviousFetchAsync(
            dispatches, "movies", "movie-never-touched", CancellationToken.None));
    }

    // ------------------------------------------------------------------ helpers

    private static readonly FixedTimeProvider Clock = new(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));

    private static async Task<TestStorage> StorageAsync()
    {
        var storage = TestStorage.Create();
        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, Clock),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return storage;
    }

    private static async Task InsertAsync(
        IDelunoDatabaseConnectionFactory factory,
        string dispatchId,
        string entityId,
        string releaseName,
        string? importStatus)
    {
        await using var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs, CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO download_dispatches (
                id, library_id, media_type, entity_type, entity_id, release_name,
                indexer_name, download_client_id, download_client_name, status,
                torrent_hash_or_item_id, import_status, import_completed_utc, created_utc
            ) VALUES (
                @id, 'movies-main', 'movies', 'movie', @entityId, @releaseName,
                'test-indexer', 'qbittorrent-main', 'qBittorrent', 'initial',
                'hash-1', @importStatus, @importCompletedUtc, '2026-09-03T10:00:00.0000000+00:00'
            )
            """;

        Add(command, "@id", dispatchId);
        Add(command, "@entityId", entityId);
        Add(command, "@releaseName", releaseName);
        Add(command, "@importStatus", (object?)importStatus ?? DBNull.Value);
        Add(command, "@importCompletedUtc", importStatus is null ? DBNull.Value : "2026-09-03T11:00:00.0000000+00:00");

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static void Add(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
