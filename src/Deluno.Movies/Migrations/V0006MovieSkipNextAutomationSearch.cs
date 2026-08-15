using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Movies.Migrations;

/// <summary>Tracks a single user-requested background-search skip without delaying manual searches.</summary>
public sealed class V0006MovieSkipNextAutomationSearch : SqliteSqlMigration
{
    public override int Version => 6;

    public override string Name => "movie_skip_next_automation_search";

    protected override string Sql =>
        """
        ALTER TABLE movie_wanted_state ADD COLUMN skip_next_automation_search INTEGER NOT NULL DEFAULT 0;
        """;
}
