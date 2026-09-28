using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Series.Migrations;

/// <summary>
/// The series half of <c>V0044MovieSubtitleAttemptHistoryScore</c> — read
/// that one for why this exists (#982).
/// </summary>
public sealed class V0051SeriesSubtitleAttemptHistoryScore : SqliteSqlMigration
{
    public override int Version => 51;

    public override string Name => "episode_subtitle_attempt_history_score";

    protected override string Sql =>
        """
        ALTER TABLE episode_subtitle_attempt_history ADD COLUMN score INTEGER NULL;
        """;
}
