using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Movies.Migrations;

/// <summary>
/// Keeps where a copy came from beside the movie file itself. The dispatch row
/// that carried the grab is archived the moment the import succeeds, so
/// provenance held only there vanishes exactly when the file arrives.
/// </summary>
public sealed class V0037MovieAcquisitionProvenance : SqliteSqlMigration
{
    public override int Version => 37;

    public override string Name => "movie_acquisition_provenance";

    protected override string Sql =>
        "ALTER TABLE media_preference_evaluations ADD COLUMN acquisition_json TEXT NULL;";
}
