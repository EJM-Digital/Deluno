using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Movies.Migrations;

public sealed class V0004MovieQualityAndReplacement : SqliteSqlMigration
{
    public override int Version => 4;

    public override string Name => "movie_quality_and_replacement";

    protected override string Sql =>
        """
        ALTER TABLE movie_wanted_state ADD COLUMN prevent_lower_quality_replacements INTEGER DEFAULT 1;
        ALTER TABLE movie_wanted_state ADD COLUMN quality_delta_last_decision INTEGER DEFAULT 0;

        CREATE INDEX IF NOT EXISTS ix_movie_wanted_state_replacement_protection
            ON movie_wanted_state (library_id, prevent_lower_quality_replacements, next_eligible_search_utc)
            WHERE prevent_lower_quality_replacements = 1;
        """;
}
