using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// #1098: the Live screen's Connections list says what each processor and Seerr server did today.
/// Both counts are a windowed, grouped read on the day's rows; these indexes cover them so the
/// read touches only today's rows. Nothing about the data changes.
/// </summary>
public sealed class V0136LiveConnectionDayIndexes : SqliteSqlMigration
{
    public override int Version => 136;

    public override string Name => "live_connection_day_indexes";

    protected override string Sql =>
        """
        CREATE INDEX IF NOT EXISTS ix_processor_handoffs_created_connection
            ON processor_handoffs (created_utc, processor_connection_id);

        CREATE INDEX IF NOT EXISTS ix_seerr_acquisitions_created_connection
            ON seerr_acquisitions (created_utc, connection_id);
        """;
}
