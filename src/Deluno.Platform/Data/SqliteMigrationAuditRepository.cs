using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Platform.Contracts;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Platform.Data;

public sealed class SqliteMigrationAuditRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider)
    : IMigrationAuditRepository
{
    public async Task<MigrationAuditReport> RecordMigrationAuditReportAsync(
        MigrationAuditReport report,
        CancellationToken cancellationToken)
    {
        var persisted = report with
        {
            Id = Guid.CreateVersion7().ToString("N"),
            AppliedUtc = timeProvider.GetUtcNow()
        };
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO migration_audit_reports (
                id, source_kind, source_name, applied_utc, preflight_report_json, result_report_json, applied_items_json
            ) VALUES (
                @id, @sourceKind, @sourceName, @appliedUtc, @preflightReportJson, @resultReportJson, @appliedItemsJson
            );
            """;
        AddParameter(command, "@id", persisted.Id);
        AddParameter(command, "@sourceKind", persisted.SourceKind);
        AddParameter(command, "@sourceName", persisted.SourceName);
        AddParameter(command, "@appliedUtc", persisted.AppliedUtc.ToString("O"));
        AddParameter(command, "@preflightReportJson", JsonSerializer.Serialize(persisted.PreflightReport));
        AddParameter(command, "@resultReportJson", JsonSerializer.Serialize(persisted.ResultReport));
        AddParameter(command, "@appliedItemsJson", JsonSerializer.Serialize(persisted.Applied));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return persisted;
    }

    public async Task<IReadOnlyList<MigrationAuditReport>> ListMigrationAuditReportsAsync(
        int take,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, source_kind, source_name, applied_utc, preflight_report_json, result_report_json, applied_items_json
            FROM migration_audit_reports
            ORDER BY applied_utc DESC
            LIMIT @take;
            """;
        AddParameter(command, "@take", Math.Clamp(take, 1, 100));
        var reports = new List<MigrationAuditReport>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            reports.Add(ReadMigrationAuditReport(reader));
        }

        return reports;
    }

    public async Task<MigrationAuditReport?> GetMigrationAuditReportAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, source_kind, source_name, applied_utc, preflight_report_json, result_report_json, applied_items_json
            FROM migration_audit_reports
            WHERE id = @id
            LIMIT 1;
            """;
        AddParameter(command, "@id", id);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadMigrationAuditReport(reader) : null;
    }

    private static MigrationAuditReport ReadMigrationAuditReport(System.Data.Common.DbDataReader reader)
    {
        var preflight = JsonSerializer.Deserialize<MigrationReport>(reader.GetString(4))
            ?? throw new InvalidOperationException("Stored migration preflight report could not be read.");
        var result = JsonSerializer.Deserialize<MigrationReport>(reader.GetString(5))
            ?? throw new InvalidOperationException("Stored migration result report could not be read.");
        var applied = JsonSerializer.Deserialize<IReadOnlyList<MigrationAppliedItem>>(reader.GetString(6)) ?? [];
        return new MigrationAuditReport(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            ParseTimestamp(reader.GetString(3)),
            preflight,
            result,
            applied);
    }
}
