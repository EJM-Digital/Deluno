using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// #1098: the latest real test of every connection (indexer, download client, processor, media server, Seerr,
/// metadata service, list provider), whoever ran it: the regular check, Test all or a Test button. System's
/// Checked column and the pushed ConnectionChecked event read and write this one row per connection.
/// </summary>
public sealed class V0048ConnectionChecks : SqliteSqlMigration
{
    public override int Version => 48;

    public override string Name => "connection_checks";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS connection_checks (
            kind TEXT NOT NULL,
            connection_id TEXT NOT NULL,
            name TEXT NOT NULL,
            ok INTEGER NOT NULL,
            slow INTEGER NOT NULL DEFAULT 0,
            message TEXT NOT NULL,
            checked_utc TEXT NOT NULL,
            PRIMARY KEY (kind, connection_id)
        );
        """;
}
