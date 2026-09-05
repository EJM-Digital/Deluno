using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using System.Data.Common;
using System.Globalization;

namespace Deluno.Jobs.Data;

public sealed class SqliteImportFailureRuleRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider) : IImportFailureRuleRepository
{
    public async Task<IReadOnlyDictionary<string, BlockDecision>> GetOverridesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT reason_code, decision FROM import_failure_rules;";

        var overrides = new Dictionary<string, BlockDecision>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            // A value this build does not understand is skipped rather than
            // thrown on. Downgrading to an older Deluno should cost you the
            // setting, not every import.
            if (Enum.TryParse<BlockDecision>(reader.GetString(1), ignoreCase: true, out var decision))
            {
                overrides[reader.GetString(0)] = decision;
            }
        }

        return overrides;
    }

    public async Task<IReadOnlyList<ImportFailureRule>> ListAsync(CancellationToken cancellationToken)
    {
        var overrides = await GetOverridesAsync(cancellationToken);

        // Driven by the policy's own list, not by what happens to be in the
        // table, so a failure kind added to the pipeline appears here the day
        // it is added rather than the first time it happens to somebody.
        return ImportFailurePolicy.KnownReasons
            .Select(reason => new ImportFailureRule(
                reason,
                ImportFailurePolicy.CategoryFor(reason),
                ImportFailurePolicy.BlockFor(reason, overrides),
                ImportFailurePolicy.BlockFor(reason)))
            .ToArray();
    }

    public async Task SetAsync(string reasonCode, BlockDecision decision, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO import_failure_rules (reason_code, decision, updated_utc)
            VALUES (@reasonCode, @decision, @updatedUtc)
            ON CONFLICT (reason_code) DO UPDATE SET
                decision = excluded.decision,
                updated_utc = excluded.updated_utc;
            """;

        Add(command, "@reasonCode", reasonCode);
        Add(command, "@decision", decision.ToString());
        Add(command, "@updatedUtc", timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ResetAsync(string reasonCode, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM import_failure_rules WHERE reason_code = @reasonCode;";
        Add(command, "@reasonCode", reasonCode);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
