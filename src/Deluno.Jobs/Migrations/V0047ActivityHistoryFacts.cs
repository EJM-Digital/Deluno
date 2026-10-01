using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// #1100: Activity becomes the full history, filterable by title, file, source and reason. Each event now
/// keeps the title, the file paths, the outside source and (for a turned-down release) the reason group it
/// concerns as columns written once when the event is recorded, so a filter reads an index instead of
/// searching every sentence. Older rows leave them empty and are still found through their message.
/// </summary>
public sealed class V0047ActivityHistoryFacts : SqliteSqlMigration
{
    public override int Version => 47;

    public override string Name => "activity_history_facts";

    protected override string Sql =>
        """
        ALTER TABLE activity_events ADD COLUMN title TEXT NULL;
        ALTER TABLE activity_events ADD COLUMN file_path TEXT NULL;
        ALTER TABLE activity_events ADD COLUMN source_name TEXT NULL;
        ALTER TABLE activity_events ADD COLUMN reason_group TEXT NULL;

        CREATE INDEX IF NOT EXISTS ix_activity_events_reason_created
            ON activity_events (reason_group, created_utc DESC, id DESC)
            WHERE reason_group IS NOT NULL;

        CREATE INDEX IF NOT EXISTS ix_activity_events_title_created
            ON activity_events (title COLLATE NOCASE, created_utc DESC, id DESC)
            WHERE title IS NOT NULL;

        CREATE INDEX IF NOT EXISTS ix_activity_events_category_created_id
            ON activity_events (category, created_utc DESC, id DESC);
        """;
}
