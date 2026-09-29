using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// #1055: a processor connection can carry an optional health-check address.
/// Deluno asks it with GET and a 2xx answer reads as healthy; without one the
/// test can only confirm something answers at the hand-off address. Empty for
/// every existing connection, so their test behaves exactly as before.
/// </summary>
public sealed class V0129ProcessorHealthCheckUrl : SqliteSqlMigration
{
    public override int Version => 129;

    public override string Name => "processor_health_check_url";

    protected override string Sql =>
        """
        ALTER TABLE processor_connections ADD COLUMN health_check_url TEXT NULL;
        """;
}
