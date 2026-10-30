using Deluno.Media;
using Microsoft.Data.Sqlite;

namespace Deluno.Persistence.Tests.Media;

/// <summary>Why a show is on the rung it is on, in the words Activity and the show page print: a show with nothing wanted or no catalogued episodes says so, and a part-held show counts.</summary>
public sealed class AShowsRungReasonReadsInPlainWordsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-01T00:00:00Z");

    private static SqliteConnection Open()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var setup = connection.CreateCommand();
        setup.CommandText = """
            CREATE TABLE episode_entries (id TEXT PRIMARY KEY, series_id TEXT NOT NULL, monitored INTEGER NOT NULL, air_date_utc TEXT, has_file INTEGER NOT NULL, quality_cutoff_met INTEGER NOT NULL DEFAULT 1);
            CREATE TABLE episode_wanted_state (episode_id TEXT PRIMARY KEY, wanted_status TEXT NOT NULL);
            CREATE TABLE series_wanted_state (series_id TEXT NOT NULL, library_id TEXT NOT NULL, wanted_status TEXT, wanted_reason TEXT, quality_cutoff_met INTEGER, updated_utc TEXT);
            INSERT INTO series_wanted_state VALUES ('s1', 'lib', 'missing', '', 0, '');
            """;
        setup.ExecuteNonQuery();
        return connection;
    }

    private static void Episode(SqliteConnection connection, string id, bool monitored, bool hasFile)
    {
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO episode_entries (id, series_id, monitored, air_date_utc, has_file) VALUES (@id, 's1', @monitored, '2026-01-01T00:00:00.0000000+00:00', @hasFile);";
        insert.Parameters.AddWithValue("@id", id);
        insert.Parameters.AddWithValue("@monitored", monitored ? 1 : 0);
        insert.Parameters.AddWithValue("@hasFile", hasFile ? 1 : 0);
        insert.ExecuteNonQuery();
    }

    private static async Task<string> ReasonAfterRefreshAsync(SqliteConnection connection)
    {
        await SeriesRungWriter.RefreshAsync(connection, null, "s1", "lib", Now, CancellationToken.None);
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT wanted_reason FROM series_wanted_state WHERE series_id = 's1';";
        return (string)read.ExecuteScalar()!;
    }

    [Fact]
    public async Task A_show_with_no_catalogued_episodes_says_so()
    {
        using var connection = Open();

        Assert.Equal("Deluno has not catalogued this show's episodes yet.", await ReasonAfterRefreshAsync(connection));
    }

    [Fact]
    public async Task A_part_held_show_counts_what_is_here()
    {
        using var connection = Open();
        Episode(connection, "e1", monitored: true, hasFile: true);
        Episode(connection, "e2", monitored: true, hasFile: false);

        Assert.Equal("1 of 2 aired episodes are here.", await ReasonAfterRefreshAsync(connection));
    }
}
