using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Movies.Migrations;

/// <summary>
/// Every subtitle search attempt for one movie, kept append-only (#840) — a
/// person's own retry history, not just <c>movie_subtitle_attempt</c>'s one
/// row per language that decides when the next try is due. A retention cap
/// is enforced in code (<c>SqliteMediaSubtitleRepository.RecordAttemptHistoryAsync</c>)
/// rather than in SQL, so it stays the same rule for both catalogues.
/// </summary>
public sealed class V0043MovieSubtitleAttemptHistory : SqliteSqlMigration
{
    public override int Version => 43;

    public override string Name => "movie_subtitle_attempt_history";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS movie_subtitle_attempt_history (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            movie_id TEXT NOT NULL,
            language TEXT NOT NULL,
            provider TEXT NULL,
            outcome TEXT NOT NULL,
            attempted_utc TEXT NOT NULL,
            FOREIGN KEY (movie_id) REFERENCES movie_entries(id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS idx_movie_subtitle_attempt_history_movie
            ON movie_subtitle_attempt_history(movie_id, attempted_utc);
        """;
}
