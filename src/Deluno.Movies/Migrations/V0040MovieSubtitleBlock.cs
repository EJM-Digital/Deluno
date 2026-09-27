using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Movies.Migrations;

/// <summary>
/// A subtitle Deluno has decided to refuse for one movie, and the candidate
/// identity a later fetch was written with, so a refusal can name the exact
/// download it means (#897).
///
/// <para>The twin of <c>V0077SeriesSubtitleBlock</c>, same shape as every
/// other pair in this store. <c>source_key</c> lands on
/// <c>movie_subtitle_state</c> rather than a new table: it describes the row
/// that is already there, the same way <c>provider</c> already does.</para>
/// </summary>
public sealed class V0040MovieSubtitleBlock : SqliteSqlMigration
{
    public override int Version => 40;

    public override string Name => "movie_subtitle_block";

    protected override string Sql =>
        """
        ALTER TABLE movie_subtitle_state ADD COLUMN source_key TEXT NULL;

        CREATE TABLE IF NOT EXISTS movie_subtitle_block (
            movie_id TEXT NOT NULL,
            language TEXT NOT NULL,
            block_key TEXT NOT NULL,
            provider TEXT NULL,
            release_name TEXT NULL,
            reason TEXT NOT NULL,
            blocked_utc TEXT NOT NULL,
            PRIMARY KEY (movie_id, block_key),
            FOREIGN KEY (movie_id) REFERENCES movie_entries(id) ON DELETE CASCADE
        );
        """;
}
