using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// Gives missing-title and upgrade searches independent cursors and records
/// which kind of search each cycle performed. The old single cursor made a
/// library's missing queue able to starve its upgrade queue indefinitely.
/// </summary>
public sealed class V0012IndependentLibrarySearchSchedules : SqliteSqlMigration
{
    public override int Version => 12;

    public override string Name => "independent_library_search_schedules";

    protected override string Sql =>
        """
        ALTER TABLE library_automation_state ADD COLUMN next_missing_search_utc TEXT NULL;
        ALTER TABLE library_automation_state ADD COLUMN next_upgrade_search_utc TEXT NULL;
        ALTER TABLE search_cycle_runs ADD COLUMN search_kind TEXT NOT NULL DEFAULT 'combined';

        CREATE INDEX IF NOT EXISTS ix_library_automation_state_next_missing_search
            ON library_automation_state (next_missing_search_utc);

        CREATE INDEX IF NOT EXISTS ix_library_automation_state_next_upgrade_search
            ON library_automation_state (next_upgrade_search_utc);
        """;
}
