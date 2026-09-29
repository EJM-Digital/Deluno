using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// #1077: a library, a hand-off and a recorded outcome pointed at their
/// processor by its NAME, so renaming the connection orphaned every one of
/// them. They now hold the connection's id.
///
/// <para>Existing names are mapped to ids case-insensitively, as the lookup
/// always was. A row whose name matches no connection keeps the name alone (its
/// id stays empty) and is still found by name until the person picks a
/// processor. The name column stays as a snapshot for display.</para>
/// </summary>
public sealed class V0132ProcessorLinksById : SqliteSqlMigration
{
    public override int Version => 132;

    public override string Name => "processor_links_by_id";

    protected override string Sql =>
        """
        ALTER TABLE libraries ADD COLUMN processor_connection_id TEXT NULL;
        ALTER TABLE processor_handoffs ADD COLUMN processor_connection_id TEXT NULL;
        ALTER TABLE processor_handoff_outcomes ADD COLUMN processor_connection_id TEXT NULL;

        UPDATE libraries
        SET processor_connection_id = (
            SELECT pc.id FROM processor_connections pc
            WHERE pc.name = libraries.processor_name COLLATE NOCASE
            ORDER BY pc.created_utc LIMIT 1)
        WHERE processor_name IS NOT NULL AND TRIM(processor_name) <> '';

        UPDATE processor_handoffs
        SET processor_connection_id = (
            SELECT pc.id FROM processor_connections pc
            WHERE pc.name = processor_handoffs.processor_name COLLATE NOCASE
            ORDER BY pc.created_utc LIMIT 1)
        WHERE processor_name IS NOT NULL AND TRIM(processor_name) <> '';

        UPDATE processor_handoff_outcomes
        SET processor_connection_id = (
            SELECT pc.id FROM processor_connections pc
            WHERE pc.name = processor_handoff_outcomes.processor_name COLLATE NOCASE
            ORDER BY pc.created_utc LIMIT 1)
        WHERE processor_name IS NOT NULL AND TRIM(processor_name) <> '';
        """;
}
