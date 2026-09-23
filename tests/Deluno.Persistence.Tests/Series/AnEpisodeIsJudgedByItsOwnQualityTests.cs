using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Persistence.Tests.Series;

/// <summary>
/// #591: an episode search reads the episode's own quality, not the show's.
///
/// <para><b>Why this file exists.</b> It read the show's. On the soak rig
/// Breaking Bad held S01E01 and S01E03 at WEB 1080p, so every missing episode
/// was judged as if it already held WEB 1080p: a 1080p release was held for
/// review, a 720p one refused as a downgrade, and the gaps in the show were
/// never filled.</para>
/// </summary>
public sealed class AnEpisodeIsJudgedByItsOwnQualityTests
{
    private static readonly FixedTimeProvider Clock = new(DateTimeOffset.Parse("2026-09-23T12:00:00Z"));

    [Fact]
    public async Task A_missing_episode_of_a_partly_held_show_has_no_current_quality()
    {
        var (repository, storage, seriesId) = await ShowAsync();
        using var _ = storage;
        await EpisodeAsync(storage, seriesId, "e-held", 1, 1, hasFile: true, quality: "WEB 1080p");
        await EpisodeAsync(storage, seriesId, "e-missing", 1, 2, hasFile: false, quality: null);

        Assert.Null(await repository.GetEpisodeCurrentQualityAsync("e-missing", CancellationToken.None));
        Assert.Equal("WEB 1080p", await repository.GetEpisodeCurrentQualityAsync("e-held", CancellationToken.None));
    }

    /// <summary>
    /// An episode that holds a file but has no quality recorded against it
    /// (older rows) still reads as holding something, so a search does not
    /// treat a held file as missing.
    /// </summary>
    [Fact]
    public async Task A_held_episode_with_no_quality_of_its_own_falls_back_to_the_show()
    {
        var (repository, storage, seriesId) = await ShowAsync();
        using var _ = storage;
        await EpisodeAsync(storage, seriesId, "e-old", 1, 1, hasFile: true, quality: null);

        Assert.Equal("WEB 1080p", await repository.GetEpisodeCurrentQualityAsync("e-old", CancellationToken.None));
    }

    private static async Task<(SqliteSeriesCatalogRepository Repository, TestStorage Storage, string SeriesId)> ShowAsync()
    {
        var storage = TestStorage.Create();
        await new SeriesSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, Clock), NullLogger<SeriesSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        var repository = new SqliteSeriesCatalogRepository(storage.Factory, Clock);
        var series = await repository.AddAsync(new CreateSeriesRequest("Breaking Bad", 2008, "tt0903747"), CancellationToken.None);
        await repository.EnsureWantedStateAsync(series.Id, "lib-tv", "missing", "Some episodes are missing.", true, "WEB 1080p", "WEB 1080p", false, CancellationToken.None);
        return (repository, storage, series.Id);
    }

    private static async Task EpisodeAsync(TestStorage storage, string seriesId, string id, int season, int episode, bool hasFile, string? quality)
    {
        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Series, CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO episode_entries (id, series_id, season_number, episode_number, monitored, has_file, created_utc, updated_utc)
            VALUES (@id, @seriesId, @season, @episode, 1, @hasFile, @now, @now);
            INSERT INTO episode_wanted_state (episode_id, series_id, library_id, wanted_status, wanted_reason, current_quality, updated_utc)
            VALUES (@id, @seriesId, 'lib-tv', @status, 'test', @quality, @now);
            """;
        AddParameter(command, "@id", id);
        AddParameter(command, "@seriesId", seriesId);
        AddParameter(command, "@season", season);
        AddParameter(command, "@episode", episode);
        AddParameter(command, "@hasFile", hasFile ? 1 : 0);
        AddParameter(command, "@status", hasFile ? "covered" : "missing");
        AddParameter(command, "@quality", quality);
        AddParameter(command, "@now", Clock.GetUtcNow().ToString("O"));
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
