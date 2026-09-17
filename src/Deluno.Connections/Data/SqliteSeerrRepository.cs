using System.Data.Common;
using Deluno.Connections.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Security;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Connections.Data;

public interface ISeerrRepository
{
    Task<IReadOnlyList<SeerrConnection>> ListAsync(CancellationToken cancellationToken);

    Task<SeerrConnection?> GetAsync(string id, CancellationToken cancellationToken);

    /// <summary>Creates a server when <paramref name="id"/> is null, otherwise updates it.</summary>
    Task<SeerrConnection?> SaveAsync(string? id, SaveSeerrRequest request, CancellationToken cancellationToken);

    Task RecordHealthAsync(string id, SeerrTestResult result, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken);
}

/// <summary>
/// The configured Seerr servers (#522). The purpose label handed to
/// <c>ISecretProtector</c>, <c>seerr:api-key</c>, is a cryptographic label:
/// changing it makes every stored key unreadable.
/// </summary>
public sealed class SqliteSeerrRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider,
    ISecretProtector secretProtector)
    : ISeerrRepository
{
    private const string KeyPurpose = "seerr:api-key";

    private const string Columns =
        "id, name, base_url, api_key, is_enabled, health_status, last_health_message, last_health_latency_ms, last_health_test_utc, server_name, version, created_utc, updated_utc";

    public async Task<IReadOnlyList<SeerrConnection>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM seerr_servers ORDER BY name COLLATE NOCASE ASC;";
        var servers = new List<SeerrConnection>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            servers.Add(Read(reader));
        }

        return servers;
    }

    public async Task<SeerrConnection?> GetAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM seerr_servers WHERE id = @id LIMIT 1;";
        AddParameter(command, "@id", id);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<SeerrConnection?> SaveAsync(string? id, SaveSeerrRequest request, CancellationToken cancellationToken)
    {
        var existing = id is null ? null : await GetAsync(id, cancellationToken);
        if (id is not null && existing is null)
        {
            return null;
        }

        var rowId = existing?.Id ?? Guid.CreateVersion7().ToString("N");
        var now = timeProvider.GetUtcNow();
        var key = string.IsNullOrWhiteSpace(request.ApiKey) ? existing?.ApiKey : request.ApiKey.Trim();
        var addressChanged = existing is not null &&
            !string.Equals(existing.BaseUrl, NormalizeBaseUrl(request.BaseUrl), StringComparison.OrdinalIgnoreCase);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = existing is null
            ? """
              INSERT INTO seerr_servers (id, name, base_url, api_key, is_enabled, health_status, created_utc, updated_utc)
              VALUES (@id, @name, @baseUrl, @apiKey, @isEnabled, 'untested', @now, @now);
              """
            : """
              UPDATE seerr_servers
              SET name = @name,
                  base_url = @baseUrl,
                  api_key = @apiKey,
                  is_enabled = @isEnabled,
                  health_status = CASE WHEN @addressChanged = 1 THEN 'untested' ELSE health_status END,
                  server_name = CASE WHEN @addressChanged = 1 THEN NULL ELSE server_name END,
                  version = CASE WHEN @addressChanged = 1 THEN NULL ELSE version END,
                  updated_utc = @now
              WHERE id = @id;
              """;
        AddParameter(command, "@id", rowId);
        AddParameter(command, "@name", request.Name?.Trim());
        AddParameter(command, "@baseUrl", NormalizeBaseUrl(request.BaseUrl));
        AddParameter(command, "@apiKey", string.IsNullOrWhiteSpace(key) ? null : secretProtector.Protect(KeyPurpose, key));
        AddParameter(command, "@isEnabled", request.IsEnabled ? 1 : 0);
        AddParameter(command, "@addressChanged", addressChanged ? 1 : 0);
        AddParameter(command, "@now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return await GetAsync(rowId, cancellationToken);
    }

    public async Task RecordHealthAsync(string id, SeerrTestResult result, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE seerr_servers
            SET health_status = @status,
                last_health_message = @message,
                last_health_latency_ms = @latency,
                last_health_test_utc = @testedUtc,
                server_name = COALESCE(@serverName, server_name),
                version = COALESCE(@version, version)
            WHERE id = @id;
            """;
        AddParameter(command, "@id", id);
        AddParameter(command, "@status", result.Status);
        AddParameter(command, "@message", result.Message);
        AddParameter(command, "@latency", result.LatencyMs);
        AddParameter(command, "@testedUtc", timeProvider.GetUtcNow().ToString("O"));
        AddParameter(command, "@serverName", result.Ok ? result.ServerName : null);
        AddParameter(command, "@version", result.Ok ? result.Version : null);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM seerr_servers WHERE id = @id;";
        AddParameter(command, "@id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public static string NormalizeBaseUrl(string? value)
        => (value ?? string.Empty).Trim().TrimEnd('/');

    private SeerrConnection Read(DbDataReader reader)
        => new(
            Id: reader.GetString(0),
            Name: reader.GetString(1),
            BaseUrl: reader.GetString(2),
            ApiKey: reader.IsDBNull(3) ? null : secretProtector.Unprotect(KeyPurpose, reader.GetString(3)),
            IsEnabled: reader.GetInt32(4) == 1,
            HealthStatus: reader.GetString(5),
            LastHealthMessage: reader.IsDBNull(6) ? null : reader.GetString(6),
            LastHealthLatencyMs: reader.IsDBNull(7) ? null : reader.GetInt32(7),
            LastHealthTestUtc: reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)),
            ServerName: reader.IsDBNull(9) ? null : reader.GetString(9),
            Version: reader.IsDBNull(10) ? null : reader.GetString(10),
            CreatedUtc: DateTimeOffset.Parse(reader.GetString(11)),
            UpdatedUtc: DateTimeOffset.Parse(reader.GetString(12)));
}
