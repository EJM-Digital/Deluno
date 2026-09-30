using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Movies.Migrations;

/// <summary>
/// #1065: a film kept in two libraries can stop being wanted in just one of them
/// (its file was deleted there) while the other library's copy stays monitored
/// and keeps being upgraded. The flag lives on the library's own row; the
/// title's monitored flag is untouched. Nothing is flipped for existing rows.
/// </summary>
public sealed class V0049MovieNotWantedHere : SqliteSqlMigration
{
    public override int Version => 49;

    public override string Name => "movie_not_wanted_here";

    protected override string Sql =>
        """
        ALTER TABLE movie_wanted_state ADD COLUMN not_wanted_here INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE movie_wanted_state ADD COLUMN not_wanted_since_utc TEXT NULL;
        """;
}
