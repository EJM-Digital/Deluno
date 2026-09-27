using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Series.Migrations;

/// <summary>
/// The series half of <c>V0043MovieSubtitleAttemptHistory</c> — read that one
/// for why this exists (#840). Keyed by episode, the same as
/// <c>episode_subtitle_attempt</c>, never by the show.
/// </summary>
public sealed class V0050SeriesSubtitleAttemptHistory : SqliteSqlMigration
{
    public override int Version => 50;

    public override string Name => "episode_subtitle_attempt_history";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS episode_subtitle_attempt_history (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            episode_id TEXT NOT NULL,
            language TEXT NOT NULL,
            provider TEXT NULL,
            outcome TEXT NOT NULL,
            attempted_utc TEXT NOT NULL,
            FOREIGN KEY (episode_id) REFERENCES episode_entries(id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS idx_episode_subtitle_attempt_history_episode
            ON episode_subtitle_attempt_history(episode_id, attempted_utc);
        """;
}
