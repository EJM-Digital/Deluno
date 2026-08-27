using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Series.Migrations;

/// <summary>
/// What subtitles Deluno holds for an episode, and when it last looked.
///
/// The twin of <c>V0015MovieSubtitleState</c> — see that file for why the two
/// are the same shape, and for the reasoning behind the variant key and the
/// scan table.
///
/// The grain is the episode, because that is where a show's subtitles are: a
/// series has no file, and a show that is subtitled in half its episodes has to
/// be able to say so. The index is on the episode id first, so a page of fifty
/// shows reads the same range scan the episode progress rollup already does.
/// </summary>
public sealed class V0016SeriesSubtitleState : SqliteSqlMigration
{
    public override int Version => 16;

    public override string Name => "series_subtitle_state";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS episode_subtitle_state (
            episode_id TEXT NOT NULL,
            language TEXT NOT NULL,
            forced INTEGER NOT NULL DEFAULT 0,
            hearing_impaired INTEGER NOT NULL DEFAULT 0,
            source TEXT NOT NULL,
            file_path TEXT NULL,
            stream_index INTEGER NULL,
            codec TEXT NULL,
            provider TEXT NULL,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL,
            PRIMARY KEY (episode_id, language, forced, hearing_impaired),
            FOREIGN KEY (episode_id) REFERENCES episode_entries(id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS episode_subtitle_scan (
            episode_id TEXT PRIMARY KEY,
            file_path TEXT NOT NULL,
            file_size_bytes INTEGER NULL,
            probe_status TEXT NOT NULL,
            subtitle_count INTEGER NOT NULL DEFAULT 0,
            scanned_utc TEXT NOT NULL,
            FOREIGN KEY (episode_id) REFERENCES episode_entries(id) ON DELETE CASCADE
        );
        """;
}
