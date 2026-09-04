using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Lets a profile say how much it cares about each preference it selected.
///
/// <para>#394: a custom format carried one score globally, so a profile could
/// choose whether to care about HDR10 and never how much. Empty means every
/// selected preference keeps the guide's own recommendation, which is what
/// each profile had before it could answer for itself.</para>
/// </summary>
public sealed class V0055ProfileFormatIntents : SqliteSqlMigration
{
    public override int Version => 55;

    public override string Name => "profile_format_intents";

    protected override string Sql =>
        "ALTER TABLE quality_profiles ADD COLUMN format_intents_json TEXT NOT NULL DEFAULT '';";
}
