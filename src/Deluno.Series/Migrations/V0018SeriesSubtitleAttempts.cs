using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Series.Migrations;

/// <summary>
/// The series half of <c>V0017MovieSubtitleAttempts</c> — read that one for why.
///
/// <para>The one difference is a fact about the domain rather than a copy: a
/// movie's subtitle belongs to the movie and an episode's belongs to the
/// episode, so this hangs off <c>episode_entries</c>. That is the same asymmetry
/// <c>MediaTableMap</c> already carries for the subtitle store itself, and it is
/// what makes a show's backoff per episode — which is right, because a show
/// missing subtitles for one episode is not a show to stop asking about.</para>
/// </summary>
public sealed class V0018SeriesSubtitleAttempts : SqliteSqlMigration
{
    public override int Version => 18;

    public override string Name => "series_subtitle_attempts";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS episode_subtitle_attempt (
            episode_id TEXT NOT NULL,
            language TEXT NOT NULL,
            attempts INTEGER NOT NULL DEFAULT 0,
            last_search_utc TEXT NOT NULL,
            next_eligible_search_utc TEXT NOT NULL,
            last_result TEXT NULL,
            PRIMARY KEY (episode_id, language),
            FOREIGN KEY (episode_id) REFERENCES episode_entries(id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS ix_episode_subtitle_attempt_due
            ON episode_subtitle_attempt (next_eligible_search_utc, episode_id);
        """;
}
