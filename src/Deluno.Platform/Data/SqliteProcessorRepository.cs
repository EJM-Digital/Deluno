using System.Security.Cryptography;
using System.Text;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Platform.Contracts;
using Deluno.Security;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;
using static Deluno.Contracts.DelunoValueNormalizers;

namespace Deluno.Platform.Data;

public sealed class SqliteProcessorRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider,
    ISecretProtector secretProtector)
    : IProcessorRepository
{
    public async Task<IReadOnlyList<ProcessorConnectionItem>> ListProcessorConnectionsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, name, provider, submission_url, auth_header_name, secret_value, is_enabled, health_status, last_health_message, last_health_test_utc, created_utc, updated_utc FROM processor_connections ORDER BY name COLLATE NOCASE;";
        var items = new List<ProcessorConnectionItem>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadProcessorConnection(reader));
        }

        return items;
    }

    public async Task<ProcessorConnectionItem?> GetProcessorConnectionAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        return await GetProcessorConnectionAsync(connection, id, cancellationToken);
    }

    public async Task<ProcessorConnectionItem?> FindProcessorConnectionByNameAsync(string? name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, name, provider, submission_url, auth_header_name, secret_value, is_enabled, health_status, last_health_message, last_health_test_utc, created_utc, updated_utc FROM processor_connections WHERE name = @name COLLATE NOCASE LIMIT 1;";
        AddParameter(command, "@name", name.Trim());
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProcessorConnection(reader) : null;
    }

    public async Task<ProcessorConnectionItem> CreateProcessorConnectionAsync(
        CreateProcessorConnectionRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var item = new ProcessorConnectionItem(
            Guid.CreateVersion7().ToString("N"),
            NormalizeName(request.Name) ?? "Processor connection",
            NormalizeProcessorConnectionProvider(request.Provider),
            NormalizeProcessorConnectionUrl(request.SubmissionUrl) ?? string.Empty,
            NormalizeProcessorAuthHeaderName(request.AuthHeaderName),
            string.IsNullOrWhiteSpace(request.Secret) ? null : request.Secret.Trim(),
            request.IsEnabled,
            "unknown",
            null,
            null,
            now,
            now);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO processor_connections (
                id, name, provider, submission_url, auth_header_name, secret_value, is_enabled,
                health_status, last_health_message, last_health_test_utc, created_utc, updated_utc
            ) VALUES (
                @id, @name, @provider, @submissionUrl, @authHeaderName, @secretValue, @isEnabled,
                @healthStatus, NULL, NULL, @createdUtc, @updatedUtc
            );
            """;
        AddParameter(command, "@id", item.Id);
        AddParameter(command, "@name", item.Name);
        AddParameter(command, "@provider", item.Provider);
        AddParameter(command, "@submissionUrl", item.SubmissionUrl);
        AddParameter(command, "@authHeaderName", item.AuthHeaderName);
        AddParameter(command, "@secretValue", item.Secret is null ? null : secretProtector.Protect($"processor-connection:{item.Id}", item.Secret));
        AddParameter(command, "@isEnabled", item.IsEnabled ? 1 : 0);
        AddParameter(command, "@healthStatus", item.HealthStatus);
        AddParameter(command, "@createdUtc", item.CreatedUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", item.UpdatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return item;
    }

    public async Task<ProcessorConnectionItem?> UpdateProcessorConnectionAsync(
        string id,
        UpdateProcessorConnectionRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        var existing = await GetProcessorConnectionAsync(connection, id, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var name = NormalizeName(request.Name) ?? existing.Name;
        var provider = NormalizeProcessorConnectionProvider(request.Provider ?? existing.Provider);
        var submissionUrl = NormalizeProcessorConnectionUrl(request.SubmissionUrl) ?? existing.SubmissionUrl;
        var authHeaderName = NormalizeProcessorAuthHeaderName(request.AuthHeaderName ?? existing.AuthHeaderName);
        var secret = string.IsNullOrWhiteSpace(request.Secret) ? existing.Secret : request.Secret.Trim();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE processor_connections
            SET name = @name,
                provider = @provider,
                submission_url = @submissionUrl,
                auth_header_name = @authHeaderName,
                secret_value = @secretValue,
                is_enabled = @isEnabled,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;
        AddParameter(command, "@id", id.Trim());
        AddParameter(command, "@name", name);
        AddParameter(command, "@provider", provider);
        AddParameter(command, "@submissionUrl", submissionUrl);
        AddParameter(command, "@authHeaderName", authHeaderName);
        AddParameter(command, "@secretValue", secret is null ? null : secretProtector.Protect($"processor-connection:{id.Trim()}", secret));
        AddParameter(command, "@isEnabled", request.IsEnabled ? 1 : 0);
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return await GetProcessorConnectionAsync(connection, id, cancellationToken);
    }

    public async Task<bool> DeleteProcessorConnectionAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM processor_connections WHERE id = @id;";
        AddParameter(command, "@id", id.Trim());
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<ProcessorConnectionItem?> RecordProcessorConnectionHealthAsync(
        string id,
        string status,
        string? message,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE processor_connections
            SET health_status = @status,
                last_health_message = @message,
                last_health_test_utc = @testedUtc,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;
        var now = timeProvider.GetUtcNow();
        AddParameter(command, "@id", id.Trim());
        AddParameter(command, "@status", NormalizeProcessorConnectionHealth(status));
        AddParameter(command, "@message", NormalizeName(message));
        AddParameter(command, "@testedUtc", now.ToString("O"));
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            return null;
        }

        return await GetProcessorConnectionAsync(connection, id, cancellationToken);
    }

    public async Task<ProcessorHandoffItem> EnsureProcessorHandoffAsync(
        CreateProcessorHandoffRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var sourcePath = request.SourcePath.Trim();
        var sourceKey = BuildProcessorSourceKey(sourcePath);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using (var insert = connection.CreateCommand())
        {
            insert.CommandText =
                """
                INSERT INTO processor_handoffs (
                    id, library_id, media_type, client_id, queue_item_id, release_name, source_path, source_key,
                    processor_name, status, output_path, import_job_id, failure_message, created_utc, updated_utc
                ) VALUES (
                    @id, @libraryId, @mediaType, @clientId, @queueItemId, @releaseName, @sourcePath, @sourceKey,
                    @processorName, 'waiting', NULL, NULL, NULL, @createdUtc, @updatedUtc
                )
                ON CONFLICT(library_id, source_key) DO NOTHING;
                """;
            AddParameter(insert, "@id", Guid.CreateVersion7().ToString("N"));
            AddParameter(insert, "@libraryId", request.LibraryId.Trim());
            AddParameter(insert, "@mediaType", request.MediaType.Trim().ToLowerInvariant());
            AddParameter(insert, "@clientId", request.ClientId.Trim());
            AddParameter(insert, "@queueItemId", request.QueueItemId.Trim());
            AddParameter(insert, "@releaseName", request.ReleaseName.Trim());
            AddParameter(insert, "@sourcePath", sourcePath);
            AddParameter(insert, "@sourceKey", sourceKey);
            AddParameter(insert, "@processorName", NormalizeName(request.ProcessorName));
            AddParameter(insert, "@createdUtc", now.ToString("O"));
            AddParameter(insert, "@updatedUtc", now.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        return (await FindProcessorHandoffAsync(request.LibraryId, null, sourcePath, cancellationToken))
            ?? throw new InvalidOperationException("Processor hand-off could not be created or loaded.");
    }

    public async Task<ProcessorHandoffItem?> FindProcessorHandoffAsync(
        string libraryId,
        string? handoffId,
        string? sourcePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(libraryId) || (string.IsNullOrWhiteSpace(handoffId) && string.IsNullOrWhiteSpace(sourcePath)))
        {
            return null;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = !string.IsNullOrWhiteSpace(handoffId)
            ? "SELECT id, library_id, media_type, client_id, queue_item_id, release_name, source_path, processor_name, status, output_path, import_job_id, failure_message, created_utc, updated_utc FROM processor_handoffs WHERE id = @id AND library_id = @libraryId LIMIT 1;"
            : "SELECT id, library_id, media_type, client_id, queue_item_id, release_name, source_path, processor_name, status, output_path, import_job_id, failure_message, created_utc, updated_utc FROM processor_handoffs WHERE library_id = @libraryId AND source_key = @sourceKey LIMIT 1;";
        AddParameter(command, "@libraryId", libraryId.Trim());
        if (!string.IsNullOrWhiteSpace(handoffId)) AddParameter(command, "@id", handoffId.Trim());
        else AddParameter(command, "@sourceKey", BuildProcessorSourceKey(sourcePath!));
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProcessorHandoff(reader) : null;
    }

    public async Task<ProcessorHandoffItem?> GetProcessorHandoffAsync(
        string id,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, library_id, media_type, client_id, queue_item_id, release_name, source_path, processor_name, status, output_path, import_job_id, failure_message, created_utc, updated_utc FROM processor_handoffs WHERE id = @id LIMIT 1;";
        AddParameter(command, "@id", id.Trim());
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProcessorHandoff(reader) : null;
    }

    public async Task<ProcessorHandoffItem?> UpdateProcessorHandoffAsync(
        string id,
        string status,
        string? outputPath,
        string? importJobId,
        string? failureMessage,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using (var update = connection.CreateCommand())
        {
            update.CommandText =
                """
                UPDATE processor_handoffs
                SET status = @status,
                    output_path = COALESCE(@outputPath, output_path),
                    import_job_id = COALESCE(@importJobId, import_job_id),
                    failure_message = @failureMessage,
                    updated_utc = @updatedUtc
                WHERE id = @id;
                """;
            AddParameter(update, "@id", id.Trim());
            AddParameter(update, "@status", NormalizeProcessorHandoffStatus(status));
            AddParameter(update, "@outputPath", NormalizePath(outputPath));
            AddParameter(update, "@importJobId", NormalizeName(importJobId));
            AddParameter(update, "@failureMessage", NormalizeName(failureMessage));
            AddParameter(update, "@updatedUtc", timeProvider.GetUtcNow().ToString("O"));
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0) return null;
        }

        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT id, library_id, media_type, client_id, queue_item_id, release_name, source_path, processor_name, status, output_path, import_job_id, failure_message, created_utc, updated_utc FROM processor_handoffs WHERE id = @id LIMIT 1;";
        AddParameter(select, "@id", id.Trim());
        using var reader = await select.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProcessorHandoff(reader) : null;
    }

    public async Task<IReadOnlyList<ProcessorHandoffItem>> ListProcessorHandoffsAsync(
        string? libraryId,
        int take,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = string.IsNullOrWhiteSpace(libraryId)
            ? "SELECT id, library_id, media_type, client_id, queue_item_id, release_name, source_path, processor_name, status, output_path, import_job_id, failure_message, created_utc, updated_utc FROM processor_handoffs ORDER BY updated_utc DESC LIMIT @take;"
            : "SELECT id, library_id, media_type, client_id, queue_item_id, release_name, source_path, processor_name, status, output_path, import_job_id, failure_message, created_utc, updated_utc FROM processor_handoffs WHERE library_id = @libraryId ORDER BY updated_utc DESC LIMIT @take;";
        AddParameter(command, "@take", Math.Clamp(take, 1, 200));
        if (!string.IsNullOrWhiteSpace(libraryId)) AddParameter(command, "@libraryId", libraryId.Trim());
        var items = new List<ProcessorHandoffItem>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadProcessorHandoff(reader));
        return items;
    }

    private static ProcessorHandoffItem ReadProcessorHandoff(System.Data.Common.DbDataReader reader)
        => new(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetString(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11), ParseTimestamp(reader.GetString(12)), ParseTimestamp(reader.GetString(13)));

    private ProcessorConnectionItem ReadProcessorConnection(System.Data.Common.DbDataReader reader)
    {
        var id = reader.GetString(0);
        return new ProcessorConnectionItem(
            id,
            reader.GetString(1),
            NormalizeProcessorConnectionProvider(reader.GetString(2)),
            reader.GetString(3),
            NormalizeProcessorAuthHeaderName(reader.GetString(4)),
            reader.IsDBNull(5) ? null : secretProtector.Unprotect($"processor-connection:{id}", reader.GetString(5)),
            reader.GetInt64(6) == 1,
            NormalizeProcessorConnectionHealth(reader.GetString(7)),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : ParseTimestamp(reader.GetString(9)),
            ParseTimestamp(reader.GetString(10)),
            ParseTimestamp(reader.GetString(11)));
    }

    private async Task<ProcessorConnectionItem?> GetProcessorConnectionAsync(
        System.Data.Common.DbConnection connection,
        string id,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, name, provider, submission_url, auth_header_name, secret_value, is_enabled, health_status, last_health_message, last_health_test_utc, created_utc, updated_utc FROM processor_connections WHERE id = @id LIMIT 1;";
        AddParameter(command, "@id", id.Trim());
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProcessorConnection(reader) : null;
    }

    private static string BuildProcessorSourceKey(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim().ToLowerInvariant())));

    private static string NormalizeProcessorHandoffStatus(string? status)
        => status?.Trim().ToLowerInvariant() switch
        {
            "submitted" or "accepted" or "started" or "waiting" or "completed" or "failed" or "timed-out" => status.Trim().ToLowerInvariant(),
            _ => "waiting"
        };

    private static string NormalizeProcessorConnectionProvider(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "fileflows" or "fileflows-webhook" => "fileflows-webhook",
            _ => "generic-webhook"
        };

    private static string NormalizeProcessorAuthHeaderName(string? value)
        => string.IsNullOrWhiteSpace(value) ? "Authorization" : value.Trim();

    private static string? NormalizeProcessorConnectionUrl(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return uri.ToString().TrimEnd('/');
    }

    private static string NormalizeProcessorConnectionHealth(string? status)
        => status?.Trim().ToLowerInvariant() switch
        {
            "healthy" or "degraded" or "unreachable" => status.Trim().ToLowerInvariant(),
            _ => "unknown"
        };
}
