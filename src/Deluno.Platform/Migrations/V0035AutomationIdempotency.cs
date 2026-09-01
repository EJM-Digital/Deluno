using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Durable responses for supported automation requests. The request hash makes
/// reusing a key with a different body an explicit conflict instead of silently
/// returning or applying the wrong operation.
/// </summary>
public sealed class V0035AutomationIdempotency : SqliteSqlMigration
{
    public override int Version => 35;

    public override string Name => "automation_idempotency";

    protected override string Sql =>
        "CREATE TABLE IF NOT EXISTS automation_idempotency (\n"
        + "    idempotency_key TEXT PRIMARY KEY,\n"
        + "    operation TEXT NOT NULL,\n"
        + "    request_hash TEXT NOT NULL,\n"
        + "    response_json TEXT NOT NULL,\n"
        + "    created_utc TEXT NOT NULL\n"
        + ");\n"
        + "CREATE INDEX IF NOT EXISTS ix_automation_idempotency_created_utc\n"
        + "    ON automation_idempotency (created_utc);";
}
