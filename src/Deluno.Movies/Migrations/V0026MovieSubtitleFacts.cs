using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Media;

namespace Deluno.Movies.Migrations;

/// <summary>
/// Which subtitles a film holds, on the film's row, so the shelf can be asked.
/// Body shared with the series migration; see
/// <see cref="CatalogueSubtitleFactsMigrationSql"/>.
/// </summary>
public sealed class V0026MovieSubtitleFacts : SqliteSqlMigration
{
    public override int Version => 26;

    public override string Name => "movie_subtitle_facts";

    protected override string Sql => CatalogueSubtitleFactsMigrationSql.For(
        "movie_entries",
        "movie_entries",
        "movie_subtitle_state",
        "movie_id",
        // A film's subtitles hang off the film, so the key is the entry id.
        "{owner}");
}
