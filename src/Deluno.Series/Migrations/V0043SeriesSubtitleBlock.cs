using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Series.Migrations;

/// <summary>
/// The series half of <c>V0040MovieSubtitleBlock</c> — read that one for why.
/// </summary>
public sealed class V0043SeriesSubtitleBlock : SqliteSqlMigration
{
    public override int Version => 43;

    public override string Name => "episode_subtitle_block";

    protected override string Sql =>
        """
        ALTER TABLE episode_subtitle_state ADD COLUMN source_key TEXT NULL;

        CREATE TABLE IF NOT EXISTS episode_subtitle_block (
            episode_id TEXT NOT NULL,
            language TEXT NOT NULL,
            block_key TEXT NOT NULL,
            provider TEXT NULL,
            release_name TEXT NULL,
            reason TEXT NOT NULL,
            blocked_utc TEXT NOT NULL,
            PRIMARY KEY (episode_id, block_key),
            FOREIGN KEY (episode_id) REFERENCES episode_entries(id) ON DELETE CASCADE
        );
        """;
}
