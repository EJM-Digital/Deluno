using Deluno.Platform.Migrations;
using Microsoft.Data.Sqlite;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// #1484: a torrent indexer with a blank Minimum seeders is set to 1 when the install updates; one with a value
/// keeps it (0 included), and a Usenet indexer stays blank.
/// </summary>
public sealed class IndexerMinimumSeedersDefaultOneMigrationTests
{
    [Fact]
    public async Task A_blank_torrent_minimum_becomes_1_and_every_other_value_is_kept()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE indexer_sources (id TEXT PRIMARY KEY, protocol TEXT NOT NULL, minimum_seeders INTEGER NULL);" +
                "INSERT INTO indexer_sources VALUES ('blank-torznab','torznab',NULL),('blank-tracker','tracker',NULL),('blank-rss','rss',NULL)," +
                "('zero','torznab',0),('five','torznab',5),('blank-usenet','newznab',NULL),('set-usenet','newznab',2);";
            await create.ExecuteNonQueryAsync();
        }

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await new V0143IndexerMinimumSeedersDefaultOne().UpAsync(connection, transaction, CancellationToken.None);
            await transaction.CommitAsync();
        }

        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT id, minimum_seeders FROM indexer_sources";
        var rows = new Dictionary<string, int?>();
        await using var reader = await read.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetInt32(1);

        Assert.Equal(1, rows["blank-torznab"]);
        Assert.Equal(1, rows["blank-tracker"]);
        Assert.Equal(1, rows["blank-rss"]);
        Assert.Equal(0, rows["zero"]);
        Assert.Equal(5, rows["five"]);
        Assert.Null(rows["blank-usenet"]);
        Assert.Equal(2, rows["set-usenet"]);
    }
}
