using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

/// <summary>
/// How hard the machine has been working (#272).
///
/// Deluno reported how full a drive was and nothing about how busy it was, so
/// when an import crawled the dashboard could not say whether the cause was
/// Deluno, the disk, or something else on the box. Shaped exactly like the
/// download throughput samples next door: one row a minute, pruned past
/// retention, indexed on the only column anything queries.
///
/// The disk columns are nullable on purpose. A whole-volume reading comes from
/// the volume itself and can be refused; that is a missing series, not a bad
/// row, and storing a zero would claim an idle disk.
/// </summary>
public sealed class V0015MachineTelemetrySamples : SqliteSqlMigration
{
    public override int Version => 15;

    public override string Name => "machine_telemetry_samples";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS machine_telemetry_samples (
            captured_utc TEXT PRIMARY KEY,
            cpu_percent REAL NOT NULL,
            memory_bytes INTEGER NOT NULL,
            total_memory_bytes INTEGER NULL,
            process_read_bytes_per_second INTEGER NOT NULL,
            process_write_bytes_per_second INTEGER NOT NULL,
            disk_busy_percent REAL NULL,
            disk_read_bytes_per_second INTEGER NULL,
            disk_write_bytes_per_second INTEGER NULL
        );

        CREATE INDEX IF NOT EXISTS idx_machine_telemetry_samples_captured
            ON machine_telemetry_samples (captured_utc DESC);
        """;
}
