using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Removes the playback-goal tables. Deluno does not model your equipment.
///
/// <para>The feature asked an owner to describe every screen, receiver and
/// player they own before it would help them, and warned them off entering a
/// model number unless they already knew its capabilities. That is homework in
/// exchange for a compatibility gate almost nobody was going to set up, and
/// the product owner removed it rather than keep polishing it.</para>
///
/// <para>V0039 and V0043 stay in the chain untouched: installed databases have
/// already run them, and deleting a migration a database has applied breaks
/// every upgrade path through it. The way to un-create a table is another
/// migration.</para>
/// </summary>
public sealed class V0050RemovePlaybackGoals : SqliteSqlMigration
{
    public override int Version => 50;

    public override string Name => "remove_playback_goals";

    protected override string Sql =>
        // Goals reference groups, and groups reference profiles, so they go in
        // that order even though SQLite would not complain either way.
        "DROP TABLE IF EXISTS playback_goals;\n"
        + "DROP TABLE IF EXISTS playback_device_groups;\n"
        + "DROP TABLE IF EXISTS playback_device_profiles;\n";
}
