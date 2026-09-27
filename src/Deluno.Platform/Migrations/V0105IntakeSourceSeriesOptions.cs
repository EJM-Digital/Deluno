using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// TV-only list options (#850): the Series Type (Standard/Daily/Anime) and
/// Season Folder override a list-added series should take on, mirroring the
/// same fields a direct series add already exposes.
///
/// Both default to NULL for every existing source, which leaves Deluno's
/// ordinary per-series defaults (Standard type, the global season-folder
/// format) exactly as they are today until a source opts in.
/// </summary>
public sealed class V0105IntakeSourceSeriesOptions : SqliteSqlMigration
{
    public override int Version => 105;

    public override string Name => "intake_source_series_options";

    protected override string Sql =>
        """
        ALTER TABLE intake_sources ADD COLUMN series_type TEXT NULL;
        ALTER TABLE intake_sources ADD COLUMN season_folder INTEGER NULL;
        """;
}
