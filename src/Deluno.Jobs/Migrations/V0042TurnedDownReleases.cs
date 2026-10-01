using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// #1098: the Live screen says how many releases Deluno looked at today and did not take, and why. A
/// search only ever kept its candidates on the title (overwritten by the next search), so nothing could
/// be counted across the day. <c>turned_down_releases</c> is the small append-only record of each one:
/// written once per search per release, read by one indexed range query, pruned after a fortnight.
/// <c>connection_uses</c> is the same for a media server refresh and a subtitle fetch, which left no
/// stored trace of their own. The category/time index serves the day's seeding clean-up count.
/// </summary>
public sealed class V0042TurnedDownReleases : SqliteSqlMigration
{
    public override int Version => 42;

    public override string Name => "turned_down_releases";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS turned_down_releases (
            id TEXT NOT NULL PRIMARY KEY,
            searched_utc TEXT NOT NULL,
            release_name TEXT NOT NULL,
            reason_group TEXT NOT NULL,
            raw_reason TEXT NULL,
            indexer_name TEXT NULL,
            title TEXT NULL,
            entity_type TEXT NULL,
            entity_id TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_turned_down_releases_searched
            ON turned_down_releases (searched_utc DESC, reason_group);

        CREATE TABLE IF NOT EXISTS connection_uses (
            id TEXT NOT NULL PRIMARY KEY,
            kind TEXT NOT NULL,
            connection_id TEXT NOT NULL,
            used_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_connection_uses_used
            ON connection_uses (used_utc, kind, connection_id);

        CREATE INDEX IF NOT EXISTS ix_activity_events_category_created
            ON activity_events (category, created_utc DESC);
        """;
}
