using System.Data.Common;
using Deluno.Infrastructure.Storage;

namespace Deluno.Jobs.Data;

public sealed class SqliteImportResolutionsRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider)
    : IImportResolutionsRepository
{
    public async Task<ImportResolution> RecordSuccessAsync(
        string dispatchId,
        string mediaType,
        string catalogId,
        string catalogItemType,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var resolutionId = $"res-{Guid.NewGuid():N}".Substring(0, 20);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO import_resolutions (
                id, dispatch_id, media_type, catalog_id, catalog_item_type,
                import_attempt_utc, import_success_utc, created_utc
            )
            VALUES (
                @id, @dispatchId, @mediaType, @catalogId, @catalogItemType,
                @importAttemptUtc, @importSuccessUtc, @createdUtc
            )
            """;

        AddParameter(command, "@id", resolutionId);
        AddParameter(command, "@dispatchId", dispatchId);
        AddParameter(command, "@mediaType", mediaType);
        AddParameter(command, "@catalogId", catalogId);
        AddParameter(command, "@catalogItemType", catalogItemType);
        AddParameter(command, "@importAttemptUtc", now.ToString("O"));
        AddParameter(command, "@importSuccessUtc", now.ToString("O"));
        AddParameter(command, "@createdUtc", now.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);

        return new ImportResolution
        {
            Id = resolutionId,
            DispatchId = dispatchId,
            MediaType = mediaType,
            CatalogId = catalogId,
            CatalogItemType = catalogItemType,
            ImportAttemptUtc = now,
            ImportSuccessUtc = now,
            CreatedUtc = now
        };
    }

    public async Task<ImportResolution> RecordFailureAsync(
        string dispatchId,
        string mediaType,
        string catalogId,
        string catalogItemType,
        string? failureCode,
        string? failureMessage,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var resolutionId = $"res-{Guid.NewGuid():N}".Substring(0, 20);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO import_resolutions (
                id, dispatch_id, media_type, catalog_id, catalog_item_type,
                import_attempt_utc, import_failure_utc, failure_code, failure_message, created_utc
            )
            VALUES (
                @id, @dispatchId, @mediaType, @catalogId, @catalogItemType,
                @importAttemptUtc, @importFailureUtc, @failureCode, @failureMessage, @createdUtc
            )
            """;

        AddParameter(command, "@id", resolutionId);
        AddParameter(command, "@dispatchId", dispatchId);
        AddParameter(command, "@mediaType", mediaType);
        AddParameter(command, "@catalogId", catalogId);
        AddParameter(command, "@catalogItemType", catalogItemType);
        AddParameter(command, "@importAttemptUtc", now.ToString("O"));
        AddParameter(command, "@importFailureUtc", now.ToString("O"));
        AddParameter(command, "@failureCode", failureCode);
        AddParameter(command, "@failureMessage", failureMessage);
        AddParameter(command, "@createdUtc", now.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);

        return new ImportResolution
        {
            Id = resolutionId,
            DispatchId = dispatchId,
            MediaType = mediaType,
            CatalogId = catalogId,
            CatalogItemType = catalogItemType,
            ImportAttemptUtc = now,
            ImportFailureUtc = now,
            FailureCode = failureCode,
            FailureMessage = failureMessage,
            CreatedUtc = now
        };
    }

    public async Task<IReadOnlyList<ImportResolution>> GetDispatchResolutionsAsync(
        string dispatchId,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, dispatch_id, media_type, catalog_id, catalog_item_type,
                import_attempt_utc, import_success_utc, import_failure_utc,
                failure_code, failure_message, created_utc
            FROM import_resolutions
            WHERE dispatch_id = @dispatchId
            ORDER BY created_utc DESC
            """;

        AddParameter(command, "@dispatchId", dispatchId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<ImportResolution>();

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadResolution(reader));
        }

        return results;
    }

    public async Task<IReadOnlyList<ImportResolution>> GetCatalogItemResolutionsAsync(
        string mediaType,
        string catalogId,
        string catalogItemType,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, dispatch_id, media_type, catalog_id, catalog_item_type,
                import_attempt_utc, import_success_utc, import_failure_utc,
                failure_code, failure_message, created_utc
            FROM import_resolutions
            WHERE media_type = @mediaType
              AND catalog_id = @catalogId
              AND catalog_item_type = @catalogItemType
            ORDER BY created_utc DESC
            """;

        AddParameter(command, "@mediaType", mediaType);
        AddParameter(command, "@catalogId", catalogId);
        AddParameter(command, "@catalogItemType", catalogItemType);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<ImportResolution>();

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadResolution(reader));
        }

        return results;
    }

    public async Task<IReadOnlyList<ImportResolution>> FindSuccessfulResolutionsSinceAsync(
        DateTimeOffset since,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, dispatch_id, media_type, catalog_id, catalog_item_type,
                import_attempt_utc, import_success_utc, import_failure_utc,
                failure_code, failure_message, created_utc
            FROM import_resolutions
            WHERE import_success_utc IS NOT NULL
              AND import_success_utc >= @since
            ORDER BY import_success_utc DESC
            LIMIT @limit
            """;

        AddParameter(command, "@since", since.ToString("O"));
        AddParameter(command, "@limit", limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<ImportResolution>();

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadResolution(reader));
        }

        return results;
    }

    public async Task<IReadOnlyList<ImportResolution>> FindFailedResolutionsSinceAsync(
        DateTimeOffset since,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, dispatch_id, media_type, catalog_id, catalog_item_type,
                import_attempt_utc, import_success_utc, import_failure_utc,
                failure_code, failure_message, created_utc
            FROM import_resolutions
            WHERE import_failure_utc IS NOT NULL
              AND import_failure_utc >= @since
            ORDER BY import_failure_utc DESC
            LIMIT @limit
            """;

        AddParameter(command, "@since", since.ToString("O"));
        AddParameter(command, "@limit", limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<ImportResolution>();

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadResolution(reader));
        }

        return results;
    }

    private static ImportResolution ReadResolution(DbDataReader reader)
    {
        var importSuccessUtcStr = reader.IsDBNull(6) ? null : reader.GetString(6);
        var importFailureUtcStr = reader.IsDBNull(7) ? null : reader.GetString(7);

        return new ImportResolution
        {
            Id = reader.GetString(0),
            DispatchId = reader.GetString(1),
            MediaType = reader.GetString(2),
            CatalogId = reader.GetString(3),
            CatalogItemType = reader.GetString(4),
            ImportAttemptUtc = DateTimeOffset.Parse(reader.GetString(5)),
            ImportSuccessUtc = importSuccessUtcStr != null ? DateTimeOffset.Parse(importSuccessUtcStr) : null,
            ImportFailureUtc = importFailureUtcStr != null ? DateTimeOffset.Parse(importFailureUtcStr) : null,
            FailureCode = reader.IsDBNull(8) ? null : reader.GetString(8),
            FailureMessage = reader.IsDBNull(9) ? null : reader.GetString(9),
            CreatedUtc = DateTimeOffset.Parse(reader.GetString(10))
        };
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
