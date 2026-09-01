using System.Globalization;
using Deluno.Infrastructure.Storage;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Platform.Data;

public sealed class SqliteAutomationIdempotencyStore(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider) : IAutomationIdempotencyStore
{
    public async Task<AutomationIdempotencyLookup> GetAsync(
        string key,
        string operation,
        string requestHash,
        CancellationToken cancellationToken)
    {
        var normalizedKey = NormalizeKey(key);
        var normalizedOperation = NormalizeOperation(operation);
        var normalizedHash = NormalizeHash(requestHash);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT operation, request_hash, response_json FROM automation_idempotency WHERE idempotency_key = @key LIMIT 1;";
        AddParameter(command, "@key", normalizedKey);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new AutomationIdempotencyLookup(false, true, null, null);
        }

        var storedOperation = reader.GetString(0);
        var storedHash = reader.GetString(1);
        return new AutomationIdempotencyLookup(
            Found: true,
            HashMatches: string.Equals(storedOperation, normalizedOperation, StringComparison.Ordinal)
                && string.Equals(storedHash, normalizedHash, StringComparison.OrdinalIgnoreCase),
            ResponseJson: reader.GetString(2),
            Operation: storedOperation);
    }

    public async Task<AutomationIdempotencyLookup> SaveAsync(
        string key,
        string operation,
        string requestHash,
        string responseJson,
        CancellationToken cancellationToken)
    {
        var normalizedKey = NormalizeKey(key);
        var normalizedOperation = NormalizeOperation(operation);
        var normalizedHash = NormalizeHash(requestHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(responseJson);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT OR IGNORE INTO automation_idempotency (idempotency_key, operation, request_hash, response_json, created_utc) VALUES (@key, @operation, @requestHash, @responseJson, @createdUtc);";
            AddParameter(insert, "@key", normalizedKey);
            AddParameter(insert, "@operation", normalizedOperation);
            AddParameter(insert, "@requestHash", normalizedHash);
            AddParameter(insert, "@responseJson", responseJson);
            AddParameter(insert, "@createdUtc", timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText =
            "SELECT operation, request_hash, response_json FROM automation_idempotency WHERE idempotency_key = @key LIMIT 1;";
        AddParameter(select, "@key", normalizedKey);
        using var reader = await select.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The automation idempotency response could not be read after it was stored.");
        }

        var storedOperation = reader.GetString(0);
        var storedHash = reader.GetString(1);
        var result = new AutomationIdempotencyLookup(
            Found: true,
            HashMatches: string.Equals(storedOperation, normalizedOperation, StringComparison.Ordinal)
                && string.Equals(storedHash, normalizedHash, StringComparison.OrdinalIgnoreCase),
            ResponseJson: reader.GetString(2),
            Operation: storedOperation);
        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static string NormalizeKey(string value)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("An idempotency key is required.", nameof(value))
            : value.Trim();

    private static string NormalizeOperation(string value)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("An operation is required.", nameof(value))
            : value.Trim().ToLowerInvariant();

    private static string NormalizeHash(string value)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A request hash is required.", nameof(value))
            : value.Trim().ToLowerInvariant();
}
