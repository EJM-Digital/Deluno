using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Movies.Migrations;

/// <summary>
/// When a film can actually be obtained, and when Deluno may start looking.
///
/// A release year cannot express "in cinemas but not yet buyable", so every
/// search for a just-released film burned a cycle against indexers that had
/// nothing. These three dates plus a per-film minimum availability let Deluno
/// wait until there is something to find.
/// </summary>
public sealed class V0007MovieReleaseDates : SqliteSqlMigration
{
    public override int Version => 7;

    public override string Name => "movie_release_dates";

    protected override string Sql =>
        """
        ALTER TABLE movie_entries ADD COLUMN in_cinemas_date TEXT NULL;
        ALTER TABLE movie_entries ADD COLUMN digital_release_date TEXT NULL;
        ALTER TABLE movie_entries ADD COLUMN physical_release_date TEXT NULL;
        -- announced | inCinemas | released. "released" means digital or physical.
        ALTER TABLE movie_entries ADD COLUMN minimum_availability TEXT NOT NULL DEFAULT 'released';

        CREATE INDEX IF NOT EXISTS ix_movie_entries_digital_release
            ON movie_entries (digital_release_date);
        """;
}
