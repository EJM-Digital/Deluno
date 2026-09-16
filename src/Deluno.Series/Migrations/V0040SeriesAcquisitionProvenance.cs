using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Series.Migrations;

/// <summary>
/// Keeps where a copy came from beside the episode file itself. The dispatch
/// row that carried the grab is archived the moment the import succeeds, so
/// provenance held only there vanishes exactly when the file arrives.
/// </summary>
public sealed class V0040SeriesAcquisitionProvenance : SqliteSqlMigration
{
    public override int Version => 40;

    public override string Name => "series_acquisition_provenance";

    protected override string Sql =>
        "ALTER TABLE media_preference_evaluations ADD COLUMN acquisition_json TEXT NULL;";
}
