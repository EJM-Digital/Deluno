using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Persistence.Tests.Series;

/// <summary>
/// #1065: an episode whose file is gone from disk is cleared, marked missing
/// and unmonitored in one transaction, matched by path, like movies. Before,
/// the file was cleared first and the episode unmonitored in a second call, so
/// a crash between them left a missing episode that was still monitored.
/// </summary>
public sealed class EpisodeFileGoneIsOneTransactionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-30T12:00:00Z");
    private const string LibraryId = "lib-tv-1065";
    private const string FilePath = "/media/tv/Show/S01E01.mkv";

    [Fact]
    public async Task The_file_status_and_monitored_flag_change_together()
    {
        var (repository, storage, seriesId, episodeId) = await SeededAsync();
        using var _ = storage;

        Assert.True(await repository.MarkTrackedFileGoneAsync(seriesId, episodeId, LibraryId, FilePath, CancellationToken.None));

        var row = await ReadAsync(storage, episodeId);
        Assert.Equal((0L, 0L, "missing"), row);
    }

    [Fact]
    public async Task A_path_that_no_longer_matches_changes_nothing_at_all()
    {
        var (repository, storage, seriesId, episodeId) = await SeededAsync();
        using var _ = storage;

        // A newer import replaced the file since it was read.
        Assert.False(await repository.MarkTrackedFileGoneAsync(seriesId, episodeId, LibraryId, "/media/tv/Show/other.mkv", CancellationToken.None));

        var row = await ReadAsync(storage, episodeId);
        Assert.Equal((1L, 1L, "covered"), row);
    }

    private static async Task<(long HasFile, long Monitored, string Status)> ReadAsync(TestStorage storage, string episodeId)
    {
        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Series, CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT e.has_file, e.monitored, w.wanted_status
            FROM episode_entries e JOIN episode_wanted_state w ON w.episode_id = e.id
            WHERE e.id = @id;
            """;
        AddParameter(command, "@id", episodeId);
        using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None));
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2));
    }

    private static async Task<(SqliteSeriesCatalogRepository Repository, TestStorage Storage, string SeriesId, string EpisodeId)> SeededAsync()
    {
        var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(Now);
        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await repository.AddAsync(new CreateSeriesRequest("Show", 2023, "tt1065"), CancellationToken.None);
        await repository.SyncEpisodeCatalogueAsync(
            series.Id,
            [new CatalogueEpisodeItem(1, 1, "Pilot", null, Now.AddDays(-30))],
            "provider",
            CancellationToken.None);

        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Series, CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE episode_entries
            SET has_file = 1, monitored = 1, file_path = @filePath, file_size_bytes = 4096
            WHERE series_id = @seriesId AND season_number = 1 AND episode_number = 1;

            INSERT INTO episode_wanted_state (
                episode_id, series_id, library_id, wanted_status, wanted_reason,
                current_quality, target_quality, quality_cutoff_met, updated_utc
            )
            SELECT id, @seriesId, @libraryId, 'covered', 'imported', 'WEB 1080p', 'WEB 1080p', 1, @now
            FROM episode_entries
            WHERE series_id = @seriesId AND season_number = 1 AND episode_number = 1
            ON CONFLICT(episode_id) DO UPDATE SET wanted_status = 'covered';
            """;
        AddParameter(command, "@seriesId", series.Id);
        AddParameter(command, "@libraryId", LibraryId);
        AddParameter(command, "@filePath", FilePath);
        AddParameter(command, "@now", Now.ToString("O"));
        await command.ExecuteNonQueryAsync(CancellationToken.None);

        var episodeId = await ScalarAsync(connection, series.Id);
        return (repository, storage, series.Id, episodeId);
    }

    private static async Task<string> ScalarAsync(System.Data.Common.DbConnection connection, string seriesId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM episode_entries WHERE series_id = @s AND season_number = 1 AND episode_number = 1;";
        AddParameter(command, "@s", seriesId);
        return (string)(await command.ExecuteScalarAsync(CancellationToken.None))!;
    }
}
