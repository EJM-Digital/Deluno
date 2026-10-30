using Deluno.Media;
using Microsoft.Data.Sqlite;

namespace Deluno.Persistence.Tests.Media;

/// <summary>
/// An episode is "upgradable" from its own wanted-state row, not from the flag on the episode row (which is only ever written as 0 when the
/// episode is catalogued); an episode with no wanted row at all falls back to that flag.
/// </summary>
public sealed class AnEpisodeIsUpgradableFromItsOwnWantedRowTests
{
    [Theory]
    [InlineData("upgrade", 0, true)]
    [InlineData("upgrade", 1, true)]
    [InlineData("covered", 0, false)]
    [InlineData("covered", 1, false)]
    [InlineData("missing", 0, false)]
    [InlineData(null, 0, true)]
    [InlineData(null, 1, false)]
    public void The_wanted_row_decides_and_the_episode_flag_is_only_the_fallback(string? wantedStatus, int cutoffMetFlag, bool upgradable)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                CREATE TABLE episode_entries (id TEXT PRIMARY KEY, quality_cutoff_met INTEGER NOT NULL);
                CREATE TABLE episode_wanted_state (episode_id TEXT PRIMARY KEY, wanted_status TEXT NOT NULL);
                """;
            setup.ExecuteNonQuery();
        }

        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO episode_entries VALUES ('e1', @flag);";
            insert.Parameters.AddWithValue("@flag", cutoffMetFlag);
            insert.ExecuteNonQuery();
        }

        if (wantedStatus is not null)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO episode_wanted_state VALUES ('e1', @status);";
            insert.Parameters.AddWithValue("@status", wantedStatus);
            insert.ExecuteNonQuery();
        }

        using var query = connection.CreateCommand();
        query.CommandText = $"SELECT CASE WHEN {SeriesRungWriter.EpisodeIsUpgradable("e", "ews")} THEN 1 ELSE 0 END FROM episode_entries e LEFT JOIN episode_wanted_state ews ON ews.episode_id = e.id;";

        Assert.Equal(upgradable, Convert.ToInt32(query.ExecuteScalar()) == 1);
    }
}
