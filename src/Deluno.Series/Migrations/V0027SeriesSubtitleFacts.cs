using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Media;

namespace Deluno.Series.Migrations;

/// <summary>
/// Which subtitles a show holds, rolled up from its episodes.
///
/// <para>Subtitles are held per episode and the shelf asks about the series, so
/// this is the distinct set across every episode — <b>a language any episode
/// has</b>, which is what the filter's own wording says. "Every episode has
/// English" is a different and stricter question that belongs on the show page,
/// where the per-episode view already answers it.</para>
/// </summary>
public sealed class V0027SeriesSubtitleFacts : SqliteSqlMigration
{
    public override int Version => 27;

    public override string Name => "series_subtitle_facts";

    protected override string Sql => CatalogueSubtitleFactsMigrationSql.For(
        "series_entries",
        "series_entries",
        "episode_subtitle_state",
        "episode_id",
        "SELECT series_id FROM episode_entries WHERE id = {owner}");
}
