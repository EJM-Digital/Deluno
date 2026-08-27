using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Series;

/// <summary>
/// Covers <see cref="SqliteSeriesCatalogRepository.SyncEpisodeCatalogueAsync"/> and the
/// wanted/eligible query surface. This is the precondition ADR-001/#118 need before the
/// movie and series engines can be merged: without it, a behavioural regression in the
/// series catalogue sync would ship silently while the movie tests stayed green.
/// </summary>
public sealed class SeriesCatalogueSyncPersistenceTests
{
    private static async Task<(TestStorage Storage, SqliteSeriesCatalogRepository Repository, FixedTimeProvider TimeProvider, string SeriesId)> CreateSeriesAsync(
        DateTimeOffset now)
    {
        var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(now);

        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await repository.AddAsync(
            new CreateSeriesRequest("Severance", 2022, "tt11280740"),
            CancellationToken.None);

        return (storage, repository, timeProvider, series.Id);
    }

    [Fact]
    public async Task SyncEpisodeCatalogueAsync_ReSync_updates_metadata_but_never_clobbers_disk_state()
    {
        var now = DateTimeOffset.Parse("2026-04-29T03:00:00Z");
        var (storage, repository, _, seriesId) = await CreateSeriesAsync(now);
        using var _ = storage;

        // First sync — two episodes for one season.
        await repository.SyncEpisodeCatalogueAsync(
            seriesId,
            [
                new CatalogueEpisodeItem(1, 1, "Good News About Hell", null, now.AddDays(-30)),
                new CatalogueEpisodeItem(1, 2, "Half Loop", null, now.AddDays(-23)),
            ],
            source: "tmdb",
            CancellationToken.None);

        // Simulate disk state: episode 1 has a file and is imported; episode 2 is unmonitored
        // by the user. Both are recorded through ImportExistingAsync / UpdateEpisodeMonitoredAsync,
        // the same paths real disk-state changes go through.
        var episodeIds = await GetEpisodeIdsAsync(storage, seriesId);

        await repository.ImportExistingAsync(
            libraryId: "series-main",
            title: "Severance",
            startYear: 2022,
            wantedStatus: "covered",
            wantedReason: "Current file is accepted.",
            currentQuality: "WEB 1080p",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: true,
            unmonitorWhenCutoffMet: true,
            filePath: @"C:\media\severance\s01e01.mkv",
            fileSizeBytes: 123456,
            episodes: [new ImportedEpisodeItem(1, 1, HasFile: true, FilePath: @"C:\media\severance\s01e01.mkv", FileSizeBytes: 123456)],
            CancellationToken.None);

        await repository.UpdateEpisodeMonitoredAsync([episodeIds[(1, 2)]], monitored: false, CancellationToken.None);

        var beforeResync = await ReadEpisodeStateAsync(storage, episodeIds[(1, 1)]);
        Assert.True(beforeResync.HasFile);
        Assert.Equal(@"C:\media\severance\s01e01.mkv", beforeResync.FilePath);
        Assert.NotNull(beforeResync.ImportedUtc);

        // Re-sync with changed titles, as a metadata refresh would produce.
        await repository.SyncEpisodeCatalogueAsync(
            seriesId,
            [
                new CatalogueEpisodeItem(1, 1, "Good News About Hell (Renamed)", "An overview.", now.AddDays(-30)),
                new CatalogueEpisodeItem(1, 2, "Half Loop (Renamed)", null, now.AddDays(-23)),
            ],
            source: "tmdb",
            CancellationToken.None);

        var episode1 = await ReadEpisodeStateAsync(storage, episodeIds[(1, 1)]);
        var episode2 = await ReadEpisodeStateAsync(storage, episodeIds[(1, 2)]);

        // Titles/overview updated by the sync...
        Assert.Equal("Good News About Hell (Renamed)", episode1.Title);
        Assert.Equal("An overview.", episode1.Overview);
        Assert.Equal("Half Loop (Renamed)", episode2.Title);

        // ...but disk state from before the re-sync is untouched, per the doc comment at
        // SqliteSeriesCatalogRepository.cs:1988-1996.
        Assert.True(episode1.HasFile);
        Assert.Equal(@"C:\media\severance\s01e01.mkv", episode1.FilePath);
        Assert.NotNull(episode1.ImportedUtc);
        Assert.False(episode1.QualityCutoffMet); // sync never sets this true; only workflow evaluation does.
        Assert.True(episode1.Monitored);

        Assert.False(episode2.HasFile);
        Assert.False(episode2.Monitored); // the user's unmonitor decision must survive the re-sync.
    }

    [Fact]
    public async Task ListParentSeriesIdsAsync_returns_only_the_distinct_parents_of_selected_episodes()
    {
        var now = DateTimeOffset.Parse("2026-04-29T03:00:00Z");
        var (storage, repository, _, seriesId) = await CreateSeriesAsync(now);
        using var _ = storage;

        await repository.SyncEpisodeCatalogueAsync(
            seriesId,
            [new CatalogueEpisodeItem(1, 1, "Good News About Hell", null, now.AddDays(-30))],
            source: "tmdb",
            CancellationToken.None);

        var episodeId = (await GetEpisodeIdsAsync(storage, seriesId))[(1, 1)];
        var parentSeriesIds = await repository.ListParentSeriesIdsAsync(
            [episodeId, "missing-episode", episodeId],
            CancellationToken.None);

        Assert.Equal([seriesId], parentSeriesIds);
    }

    [Fact]
    public async Task SyncEpisodeCatalogueAsync_groups_out_of_order_episodes_into_one_row_per_distinct_season()
    {
        var now = DateTimeOffset.Parse("2026-04-29T03:00:00Z");
        var (storage, repository, _, seriesId) = await CreateSeriesAsync(now);
        using var _ = storage;

        var result = await repository.SyncEpisodeCatalogueAsync(
            seriesId,
            [
                new CatalogueEpisodeItem(3, 1, "S3E1", null, now.AddDays(-3)),
                new CatalogueEpisodeItem(1, 1, "S1E1", null, now.AddDays(-30)),
                new CatalogueEpisodeItem(2, 1, "S2E1", null, now.AddDays(-15)),
                new CatalogueEpisodeItem(1, 2, "S1E2", null, now.AddDays(-28)),
                new CatalogueEpisodeItem(2, 2, "S2E2", null, now.AddDays(-14)),
                new CatalogueEpisodeItem(3, 2, "S3E2", null, now.AddDays(-2)),
            ],
            source: "tmdb",
            CancellationToken.None);

        Assert.Equal(3, result.SeasonCount);
        Assert.Equal(6, result.EpisodeCount);
        Assert.Equal(6, result.AddedCount);
        Assert.Equal(0, result.UpdatedCount);

        await using var connection = await storage.Factory.OpenConnectionAsync("series", CancellationToken.None);

        using (var seasons = connection.CreateCommand())
        {
            seasons.CommandText = "SELECT COUNT(*) FROM season_entries WHERE series_id = @seriesId;";
            AddParam(seasons, "@seriesId", seriesId);
            Assert.Equal(3L, (long)(await seasons.ExecuteScalarAsync())!);
        }

        using var episodes = connection.CreateCommand();
        episodes.CommandText =
            """
            SELECT e.season_number, COUNT(*), COUNT(DISTINCT e.season_id)
            FROM episode_entries e
            WHERE e.series_id = @seriesId
            GROUP BY e.season_number;
            """;
        AddParam(episodes, "@seriesId", seriesId);
        using var reader = await episodes.ExecuteReaderAsync();
        var seasonsSeen = 0;
        while (await reader.ReadAsync())
        {
            seasonsSeen++;
            Assert.Equal(2L, reader.GetInt64(1));
            // Every episode in a season must attach to exactly one season row.
            Assert.Equal(1L, reader.GetInt64(2));
        }

        Assert.Equal(3, seasonsSeen);
    }

    [Fact]
    public async Task SyncEpisodeCatalogueAsync_leaves_specials_unmonitored_but_monitors_real_seasons()
    {
        var now = DateTimeOffset.Parse("2026-04-29T03:00:00Z");
        var (storage, repository, _, seriesId) = await CreateSeriesAsync(now);
        using var _ = storage;

        await repository.SyncEpisodeCatalogueAsync(
            seriesId,
            [
                // Season 0 is specials: extras and recaps most people do not
                // want hunted, so they arrive unmonitored (#243).
                new CatalogueEpisodeItem(0, 1, "Behind the scenes", null, now.AddDays(-40)),
                new CatalogueEpisodeItem(0, 2, "Recap", null, now.AddDays(-39)),
                new CatalogueEpisodeItem(1, 1, "S1E1", null, now.AddDays(-30)),
                new CatalogueEpisodeItem(1, 2, "S1E2", null, now.AddDays(-28)),
            ],
            source: "tmdb",
            CancellationToken.None);

        await using var connection = await storage.Factory.OpenConnectionAsync("series", CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT season_number, SUM(monitored)
            FROM episode_entries
            WHERE series_id = @seriesId
            GROUP BY season_number
            ORDER BY season_number;
            """;
        AddParam(command, "@seriesId", seriesId);

        var monitoredBySeason = new Dictionary<long, long>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            monitoredBySeason[reader.GetInt64(0)] = reader.GetInt64(1);
        }

        Assert.Equal(0L, monitoredBySeason[0]);
        Assert.Equal(2L, monitoredBySeason[1]);
    }

    [Fact]
    public async Task SyncEpisodeCatalogueAsync_backfills_wanted_state_idempotently()
    {
        var now = DateTimeOffset.Parse("2026-04-29T03:00:00Z");
        var (storage, repository, _, seriesId) = await CreateSeriesAsync(now);
        using var _ = storage;

        await repository.EnsureWantedStateAsync(
            seriesId,
            libraryId: "series-main",
            wantedStatus: "missing",
            wantedReason: "No accepted episodes exist.",
            hasFile: false,
            currentQuality: null,
            targetQuality: "WEB 1080p",
            qualityCutoffMet: false,
            CancellationToken.None);

        await repository.SyncEpisodeCatalogueAsync(
            seriesId,
            [new CatalogueEpisodeItem(1, 1, "Good News About Hell", null, now.AddDays(-30))],
            source: "tmdb",
            CancellationToken.None);

        var updated = await repository.ReevaluateLibraryWantedStateAsync(
            "series-main", "WEB 1080p", upgradeUntilCutoff: true, upgradeUnknownItems: false, CancellationToken.None);
        Assert.Equal(1, updated);

        await using (var connection = await storage.Factory.OpenConnectionAsync("series", CancellationToken.None))
        {
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM episode_wanted_state WHERE series_id = @seriesId;";
            AddParam(count, "@seriesId", seriesId);
            Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);

            using var seriesStateCount = connection.CreateCommand();
            seriesStateCount.CommandText =
                "SELECT COUNT(*) FROM series_wanted_state WHERE series_id = @seriesId AND library_id = @libraryId;";
            AddParam(seriesStateCount, "@seriesId", seriesId);
            AddParam(seriesStateCount, "@libraryId", "series-main");
            Assert.Equal(1L, (long)(await seriesStateCount.ExecuteScalarAsync())!);
        }

        // Re-running the reevaluation must not create a second row per series/library.
        var updatedAgain = await repository.ReevaluateLibraryWantedStateAsync(
            "series-main", "WEB 1080p", upgradeUntilCutoff: true, upgradeUnknownItems: false, CancellationToken.None);
        Assert.Equal(1, updatedAgain);

        await using var verifyConnection = await storage.Factory.OpenConnectionAsync("series", CancellationToken.None);
        using var verify = verifyConnection.CreateCommand();
        verify.CommandText = "SELECT COUNT(*) FROM series_wanted_state WHERE series_id = @seriesId;";
        AddParam(verify, "@seriesId", seriesId);
        Assert.Equal(1L, (long)(await verify.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task ListEligibleWantedAsync_excludes_files_present_unmonitored_and_deferred_series()
    {
        var now = DateTimeOffset.Parse("2026-08-13T00:00:00Z");
        var (storage, repository, _, wantedSeriesId) = await CreateSeriesAsync(now);
        using var _ = storage;

        await repository.EnsureWantedStateAsync(
            wantedSeriesId, "series-main", "missing", "Needs episodes.", false, null, "WEB 1080p", false, CancellationToken.None);

        // A second series that already has a file — must not appear.
        var coveredSeries = await repository.AddAsync(new CreateSeriesRequest("Silo", 2023, "tt14688458"), CancellationToken.None);
        await repository.EnsureWantedStateAsync(
            coveredSeries.Id, "series-main", "covered", "Current file is accepted.", true, "WEB 1080p", "WEB 1080p", true, CancellationToken.None);

        // A third, unmonitored series — excluded unless the retry window is ignored.
        var unmonitoredSeries = await repository.AddAsync(new CreateSeriesRequest("Andor", 2022, "tt9253284"), CancellationToken.None);
        await repository.EnsureWantedStateAsync(
            unmonitoredSeries.Id, "series-main", "missing", "Needs episodes.", false, null, "WEB 1080p", false, CancellationToken.None);
        await repository.UpdateMonitoredAsync([unmonitoredSeries.Id], monitored: false, CancellationToken.None);

        // A fourth series, deferred into the future — excluded unless retry window is ignored.
        var deferredSeries = await repository.AddAsync(new CreateSeriesRequest("Shogun", 2024, "tt2788316"), CancellationToken.None);
        await repository.EnsureWantedStateAsync(
            deferredSeries.Id, "series-main", "missing", "Needs episodes.", false, null, "WEB 1080p", false, CancellationToken.None);
        await repository.DeferWantedSearchAsync(deferredSeries.Id, "series-main", now.AddHours(24), CancellationToken.None);

        var eligible = await repository.ListEligibleWantedAsync("series-main", 10, now, ignoreRetryWindow: false, CancellationToken.None);
        var eligibleIgnoringRetry = await repository.ListEligibleWantedAsync("series-main", 10, now, ignoreRetryWindow: true, CancellationToken.None);

        Assert.Single(eligible);
        Assert.Equal(wantedSeriesId, eligible[0].SeriesId);

        // Ignoring the retry window still excludes the series with a file, but includes the
        // unmonitored/deferred ones — those are retry-window/monitoring skips, not "has file" skips.
        Assert.DoesNotContain(eligibleIgnoringRetry, item => item.SeriesId == coveredSeries.Id);
        Assert.Contains(eligibleIgnoringRetry, item => item.SeriesId == wantedSeriesId);
        Assert.Contains(eligibleIgnoringRetry, item => item.SeriesId == unmonitoredSeries.Id);
        Assert.Contains(eligibleIgnoringRetry, item => item.SeriesId == deferredSeries.Id);
    }

    [Fact]
    public async Task ListEligibleWantedEpisodesAsync_excludes_unmonitored_and_not_yet_eligible_episodes()
    {
        var now = DateTimeOffset.Parse("2026-08-13T00:00:00Z");
        var (storage, repository, _, seriesId) = await CreateSeriesAsync(now);
        using var _ = storage;

        // The catalogue-sync backfill only creates an episode_wanted_state row when a
        // series_wanted_state row already exists for the library, so seed that first.
        await repository.EnsureWantedStateAsync(
            seriesId, "series-main", "missing", "Needs episodes.", false, null, "WEB 1080p", false, CancellationToken.None);

        await repository.SyncEpisodeCatalogueAsync(
            seriesId,
            [
                new CatalogueEpisodeItem(1, 1, "Ep 1", null, now.AddDays(-10)),
                new CatalogueEpisodeItem(1, 2, "Ep 2", null, now.AddDays(-9)),
                new CatalogueEpisodeItem(1, 3, "Ep 3", null, now.AddDays(-8)),
            ],
            source: "tmdb",
            CancellationToken.None);

        var episodeIds = await GetEpisodeIdsAsync(storage, seriesId);

        // The query considers wanted_status IN ('missing','upgrade'). Episode 1 is
        // missing and eligible now, episode 2 missing but not eligible until later,
        // and episode 3 unmonitored entirely.
        await using (var connection = await storage.Factory.OpenConnectionAsync("series", CancellationToken.None))
        {
            await SetEpisodeWantedStatusAsync(connection, episodeIds[(1, 1)], "series-main", "missing", nextEligibleSearchUtc: null);
            await SetEpisodeWantedStatusAsync(connection, episodeIds[(1, 2)], "series-main", "missing", nextEligibleSearchUtc: now.AddHours(6));
            await SetEpisodeWantedStatusAsync(connection, episodeIds[(1, 3)], "series-main", "missing", nextEligibleSearchUtc: null);
        }

        await repository.UpdateEpisodeMonitoredAsync([episodeIds[(1, 3)]], monitored: false, CancellationToken.None);

        var eligible = await repository.ListEligibleWantedEpisodesAsync("series-main", 10, now, CancellationToken.None);

        Assert.Single(eligible);
        Assert.Equal(episodeIds[(1, 1)], eligible[0].EpisodeId);
    }

    [Fact]
    public async Task ListWantedEpisodesAsync_excludes_episodes_that_already_have_a_file()
    {
        var now = DateTimeOffset.Parse("2026-08-13T00:00:00Z");
        var (storage, repository, _, seriesId) = await CreateSeriesAsync(now);
        using var _ = storage;

        await repository.SyncEpisodeCatalogueAsync(
            seriesId,
            [
                new CatalogueEpisodeItem(1, 1, "Ep 1", null, now.AddDays(-10)),
                new CatalogueEpisodeItem(1, 2, "Ep 2", null, now.AddDays(-9)),
            ],
            source: "tmdb",
            CancellationToken.None);

        await repository.ImportExistingAsync(
            libraryId: "series-main",
            title: "Severance",
            startYear: 2022,
            wantedStatus: "covered",
            wantedReason: "Current file is accepted.",
            currentQuality: "WEB 1080p",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: true,
            unmonitorWhenCutoffMet: false,
            filePath: @"C:\media\severance\s01e01.mkv",
            fileSizeBytes: 123456,
            episodes: [new ImportedEpisodeItem(1, 1, HasFile: true, FilePath: @"C:\media\severance\s01e01.mkv", FileSizeBytes: 123456)],
            CancellationToken.None);

        var wanted = await repository.ListWantedEpisodesAsync(10, CancellationToken.None);

        Assert.Single(wanted);
        var episodeIds = await GetEpisodeIdsAsync(storage, seriesId);
        Assert.Equal(episodeIds[(1, 2)], wanted[0].EpisodeId);
    }

    [Fact]
    public async Task ImportExistingAsync_writes_series_and_episode_rows_for_an_existing_disk_file()
    {
        var now = DateTimeOffset.Parse("2026-04-29T03:00:00Z");
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(now);
        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);

        var created = await repository.ImportExistingAsync(
            libraryId: "series-main",
            title: "Severance",
            startYear: 2022,
            wantedStatus: "covered",
            wantedReason: "Current file is accepted.",
            currentQuality: "WEB 1080p",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: true,
            unmonitorWhenCutoffMet: false,
            filePath: @"C:\media\severance\s01e01.mkv",
            fileSizeBytes: 123456,
            episodes: [new ImportedEpisodeItem(1, 1, HasFile: true, FilePath: @"C:\media\severance\s01e01.mkv", FileSizeBytes: 123456)],
            CancellationToken.None);

        Assert.True(created);

        var series = Assert.Single(await repository.ListAsync(CancellationToken.None));
        Assert.Equal("Severance", series.Title);
        Assert.True(series.Monitored);

        var episodeIds = await GetEpisodeIdsAsync(storage, series.Id);
        Assert.True(episodeIds.ContainsKey((1, 1)));

        var episode = await ReadEpisodeStateAsync(storage, episodeIds[(1, 1)]);
        Assert.True(episode.HasFile);
        Assert.Equal(@"C:\media\severance\s01e01.mkv", episode.FilePath);
        Assert.NotNull(episode.ImportedUtc);

        // Importing the same series again must not create a duplicate series row.
        var createdAgain = await repository.ImportExistingAsync(
            libraryId: "series-main",
            title: "Severance",
            startYear: 2022,
            wantedStatus: "covered",
            wantedReason: "Current file is accepted.",
            currentQuality: "WEB 1080p",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: true,
            unmonitorWhenCutoffMet: false,
            filePath: @"C:\media\severance\s01e01.mkv",
            fileSizeBytes: 123456,
            episodes: [new ImportedEpisodeItem(1, 1, HasFile: true, FilePath: @"C:\media\severance\s01e01.mkv", FileSizeBytes: 123456)],
            CancellationToken.None);

        Assert.False(createdAgain);
        Assert.Single(await repository.ListAsync(CancellationToken.None));
    }

    private static async Task SetEpisodeWantedStatusAsync(
        System.Data.Common.DbConnection connection,
        string episodeId,
        string libraryId,
        string wantedStatus,
        DateTimeOffset? nextEligibleSearchUtc)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE episode_wanted_state
            SET wanted_status = @wantedStatus, next_eligible_search_utc = @next
            WHERE episode_id = @episodeId AND library_id = @libraryId;
            """;
        AddParam(command, "@wantedStatus", wantedStatus);
        AddParam(command, "@next", nextEligibleSearchUtc?.ToString("O"));
        AddParam(command, "@episodeId", episodeId);
        AddParam(command, "@libraryId", libraryId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<Dictionary<(int Season, int Episode), string>> GetEpisodeIdsAsync(TestStorage storage, string seriesId)
    {
        await using var connection = await storage.Factory.OpenConnectionAsync("series", CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, season_number, episode_number FROM episode_entries WHERE series_id = @seriesId;";
        AddParam(command, "@seriesId", seriesId);

        var result = new Dictionary<(int, int), string>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result[(reader.GetInt32(1), reader.GetInt32(2))] = reader.GetString(0);
        }

        return result;
    }

    private sealed record EpisodeState(
        string? Title,
        string? Overview,
        bool HasFile,
        bool QualityCutoffMet,
        string? FilePath,
        string? ImportedUtc,
        bool Monitored);

    private static async Task<EpisodeState> ReadEpisodeStateAsync(TestStorage storage, string episodeId)
    {
        await using var connection = await storage.Factory.OpenConnectionAsync("series", CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT title, overview, has_file, quality_cutoff_met, file_path, imported_utc, monitored
            FROM episode_entries WHERE id = @id;
            """;
        AddParam(command, "@id", episodeId);
        using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new EpisodeState(
            Title: reader.IsDBNull(0) ? null : reader.GetString(0),
            Overview: reader.IsDBNull(1) ? null : reader.GetString(1),
            HasFile: reader.GetInt64(2) == 1,
            QualityCutoffMet: reader.GetInt64(3) == 1,
            FilePath: reader.IsDBNull(4) ? null : reader.GetString(4),
            ImportedUtc: reader.IsDBNull(5) ? null : reader.GetString(5),
            Monitored: reader.GetInt64(6) == 1);
    }

    private static void AddParam(System.Data.Common.DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
