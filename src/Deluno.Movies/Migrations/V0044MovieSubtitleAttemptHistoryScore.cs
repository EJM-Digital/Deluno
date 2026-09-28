using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Movies.Migrations;

/// <summary>
/// Adds the score of the best candidate an attempt found (#982) — the same
/// rung <c>movie_subtitle</c> already records for a subtitle actually
/// written, kept beside the outcome so a person reading the history sees
/// not just "nothing found" or "usable fallback" but how close it came.
/// Null for a row written before this column existed, and for an attempt
/// that found nothing at all.
/// </summary>
public sealed class V0044MovieSubtitleAttemptHistoryScore : SqliteSqlMigration
{
    public override int Version => 44;

    public override string Name => "movie_subtitle_attempt_history_score";

    protected override string Sql =>
        """
        ALTER TABLE movie_subtitle_attempt_history ADD COLUMN score INTEGER NULL;
        """;
}
