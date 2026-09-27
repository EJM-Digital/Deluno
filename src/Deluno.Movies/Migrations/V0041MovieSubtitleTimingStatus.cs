using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Movies.Migrations;

/// <summary>
/// What the last timing sync attempt found for one subtitle (#922): moved,
/// already in time, or unable to decide either way — kept beside the row so
/// the title's own page can show "Timing: not checked" without a join.
///
/// <para>The twin of <c>V0044SeriesSubtitleTimingStatus</c>.</para>
/// </summary>
public sealed class V0041MovieSubtitleTimingStatus : SqliteSqlMigration
{
    public override int Version => 41;

    public override string Name => "movie_subtitle_timing_status";

    protected override string Sql =>
        """
        ALTER TABLE movie_subtitle_state ADD COLUMN timing_status TEXT NULL;
        ALTER TABLE movie_subtitle_state ADD COLUMN timing_reason TEXT NULL;
        """;
}
