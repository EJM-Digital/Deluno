using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Series;

public sealed class SeriesUpcomingEpisodePersistenceTests
{
    [Fact]
    public async Task ListUpcomingEpisodesAsync_returns_monitored_episodes_within_requested_window()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-05-13T05:00:00Z"));

        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var series = await repository.AddAsync(
            new CreateSeriesRequest("Severance", 2022, "tt11280740"),
            CancellationToken.None);

        await using var connection = await storage.Factory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            CancellationToken.None);

        var seasonId = Guid.CreateVersion7().ToString("N");
        using (var seasonCommand = connection.CreateCommand())
        {
            seasonCommand.CommandText =
                """
                INSERT INTO season_entries (id, series_id, season_number, monitored, created_utc, updated_utc)
                VALUES (@id, @seriesId, 1, 1, @createdUtc, @updatedUtc);
                """;
            AddParameter(seasonCommand, "@id", seasonId);
            AddParameter(seasonCommand, "@seriesId", series.Id);
            AddParameter(seasonCommand, "@createdUtc", timeProvider.GetUtcNow().ToString("O"));
            AddParameter(seasonCommand, "@updatedUtc", timeProvider.GetUtcNow().ToString("O"));
            await seasonCommand.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await InsertEpisodeAsync(connection, series.Id, seasonId, 1, 1, "Hello, Ms. Cobel", timeProvider.GetUtcNow().AddHours(24));
        await InsertEpisodeAsync(connection, series.Id, seasonId, 1, 2, "Out of window", timeProvider.GetUtcNow().AddHours(120));

        var upcoming = await repository.ListUpcomingEpisodesAsync(
            timeProvider.GetUtcNow(),
            timeProvider.GetUtcNow().AddHours(72),
            10,
            CancellationToken.None);

        var item = Assert.Single(upcoming);
        Assert.Equal(series.Id, item.SeriesId);
        Assert.Equal("Severance", item.Title);
        Assert.Equal(1, item.SeasonNumber);
        Assert.Equal(1, item.EpisodeNumber);
        Assert.Equal("Hello, Ms. Cobel", item.EpisodeTitle);
    }

    private static async Task InsertEpisodeAsync(
        System.Data.Common.DbConnection connection,
        string seriesId,
        string seasonId,
        int seasonNumber,
        int episodeNumber,
        string title,
        DateTimeOffset airDateUtc)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO episode_entries (
                id, series_id, season_id, season_number, episode_number, title, air_date_utc,
                monitored, has_file, quality_cutoff_met, created_utc, updated_utc
            ) VALUES (
                @id, @seriesId, @seasonId, @seasonNumber, @episodeNumber, @title, @airDateUtc,
                1, 0, 0, @createdUtc, @updatedUtc
            );
            """;
        AddParameter(command, "@id", Guid.CreateVersion7().ToString("N"));
        AddParameter(command, "@seriesId", seriesId);
        AddParameter(command, "@seasonId", seasonId);
        AddParameter(command, "@seasonNumber", seasonNumber);
        AddParameter(command, "@episodeNumber", episodeNumber);
        AddParameter(command, "@title", title);
        AddParameter(command, "@airDateUtc", airDateUtc.ToString("O"));
        AddParameter(command, "@createdUtc", airDateUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", airDateUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
