using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// #1098: the System screen says when each drive will be full and how often Deluno restarted this week.
/// Neither can be read from the machine, so each is kept as a small record. <c>drive_space_daily</c> is
/// one free-space reading per drive per day (the last of the day wins), read back over the last seven
/// days to find the pace a drive is filling at. <c>instance_starts</c> is one row each time Deluno
/// starts, counted over the last seven days. Both are pruned after a fortnight.
/// </summary>
public sealed class V0046SystemOverviewHistory : SqliteSqlMigration
{
    public override int Version => 46;

    public override string Name => "system_overview_history";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS drive_space_daily (
            volume_key TEXT NOT NULL,
            day TEXT NOT NULL,
            free_bytes INTEGER NOT NULL,
            total_bytes INTEGER NOT NULL,
            recorded_utc TEXT NOT NULL,
            PRIMARY KEY (volume_key, day)
        );

        CREATE TABLE IF NOT EXISTS instance_starts (
            started_utc TEXT NOT NULL PRIMARY KEY
        );
        """;
}
