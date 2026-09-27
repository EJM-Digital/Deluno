using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// VIP/subscription expiry tracking per private-tracker indexer (#747), with
/// a separate acknowledged-expiry marker so a dismissed warning stays
/// dismissed until the recorded date itself changes.
/// </summary>
public sealed class V0086IndexerVipExpiry : SqliteSqlMigration
{
    public override int Version => 86;

    public override string Name => "indexer_vip_expiry";

    protected override string Sql =>
        """
        ALTER TABLE indexer_sources ADD COLUMN vip_expires_utc TEXT NULL;
        ALTER TABLE indexer_sources ADD COLUMN vip_warning_acknowledged_expiry_utc TEXT NULL;
        """;
}
