using System.Data.Common;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Security;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Connections.Data;

public interface IMediaServerRepository
{
    Task<IReadOnlyList<MediaServerConnection>> ListAsync(CancellationToken cancellationToken);

    Task<MediaServerConnection?> GetAsync(string id, CancellationToken cancellationToken);

    /// <summary>Creates a server when <paramref name="id"/> is null, otherwise updates it.</summary>
    Task<MediaServerConnection?> SaveAsync(string? id, SaveMediaServerRequest request, CancellationToken cancellationToken);

    Task RecordHealthAsync(
        string id,
        MediaServerTestResult result,
        CancellationToken cancellationToken);

    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken);
}

/// <summary>
/// The configured media servers (#493).
///
/// <para>Its own repository, as the subtitle providers have theirs, rather than
/// more methods on <see cref="SqliteConnectionsRepository"/>. The purpose label
/// handed to <c>ISecretProtector</c>, <c>media-server:token</c>, is a
/// cryptographic label: changing it makes every stored token unreadable.</para>
/// </summary>
public sealed class SqliteMediaServerRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider,
    ISecretProtector secretProtector)
    : IMediaServerRepository
{
    private const string TokenPurpose = "media-server:token";

    private const string Columns =
        """
            id, kind, name, base_url, token, is_enabled, health_status, last_health_message,
            last_health_latency_ms, last_health_test_utc, server_name, server_id, created_utc, updated_utc,
            last_health_failure_json
        """;

    public async Task<IReadOnlyList<MediaServerConnection>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM media_servers ORDER BY name COLLATE NOCASE ASC;";

        var items = new List<MediaServerConnection>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(Read(reader));
        }

        return items;
    }

    public async Task<MediaServerConnection?> GetAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM media_servers WHERE id = @id LIMIT 1;";
        AddParameter(command, "@id", id);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<MediaServerConnection?> SaveAsync(string? id, SaveMediaServerRequest request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var existing = id is null ? null : await GetAsync(id, cancellationToken);
        if (id is not null && existing is null)
        {
            return null;
        }

        // A blank token keeps the stored one: the screen cannot show it, so it
        // sends blank when it was not touched.
        var token = string.IsNullOrWhiteSpace(request.Token) ? existing?.Token : request.Token.Trim();
        // A different address is a different server: what the old one said
        // about itself no longer describes this row.
        var addressChanged = existing is not null &&
            !string.Equals(existing.BaseUrl, NormalizeBaseUrl(request.BaseUrl), StringComparison.OrdinalIgnoreCase);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = existing is null
            ? """
              INSERT INTO media_servers (id, kind, name, base_url, token, is_enabled, health_status, created_utc, updated_utc)
              VALUES (@id, @kind, @name, @baseUrl, @token, @isEnabled, 'untested', @now, @now);
              """
            : """
              UPDATE media_servers
              SET kind = @kind,
                  name = @name,
                  base_url = @baseUrl,
                  token = @token,
                  is_enabled = @isEnabled,
                  health_status = CASE WHEN @addressChanged = 1 THEN 'untested' ELSE health_status END,
                  server_name = CASE WHEN @addressChanged = 1 THEN NULL ELSE server_name END,
                  server_id = CASE WHEN @addressChanged = 1 THEN NULL ELSE server_id END,
                  updated_utc = @now
              WHERE id = @id;
              """;
        var rowId = existing?.Id ?? Guid.CreateVersion7().ToString("N");
        AddParameter(command, "@id", rowId);
        AddParameter(command, "@kind", MediaServerKinds.Normalize(request.Kind) ?? existing?.Kind);
        AddParameter(command, "@name", request.Name?.Trim());
        AddParameter(command, "@baseUrl", NormalizeBaseUrl(request.BaseUrl));
        AddParameter(command, "@token", string.IsNullOrWhiteSpace(token) ? null : secretProtector.Protect(TokenPurpose, token));
        AddParameter(command, "@isEnabled", request.IsEnabled ? 1 : 0);
        AddParameter(command, "@addressChanged", addressChanged ? 1 : 0);
        AddParameter(command, "@now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return await GetAsync(rowId, cancellationToken);
    }

    public async Task RecordHealthAsync(string id, MediaServerTestResult result, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE media_servers
            SET health_status = @status,
                last_health_message = @message,
                last_health_latency_ms = @latency,
                last_health_test_utc = @testedUtc,
                server_name = COALESCE(@serverName, server_name),
                server_id = COALESCE(@serverId, server_id),
                last_health_failure_json = @failureJson
            WHERE id = @id;
            """;
        AddParameter(command, "@id", id);
        AddParameter(command, "@status", result.Status);
        AddParameter(command, "@message", result.Message);
        AddParameter(command, "@latency", result.LatencyMs);
        AddParameter(command, "@testedUtc", timeProvider.GetUtcNow().ToString("O"));
        AddParameter(command, "@serverName", result.Ok ? result.ServerName : null);
        AddParameter(command, "@serverId", result.Ok ? result.ServerId : null);
        AddParameter(command, "@failureJson", result.Failure is null ? null : JsonSerializer.Serialize(result.Failure));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM media_servers WHERE id = @id;";
        AddParameter(command, "@id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public static string NormalizeBaseUrl(string? value)
        => (value ?? string.Empty).Trim().TrimEnd('/');

    private MediaServerConnection Read(DbDataReader reader)
        => new(
            Id: reader.GetString(0),
            Kind: reader.GetString(1),
            Name: reader.GetString(2),
            BaseUrl: reader.GetString(3),
            Token: reader.IsDBNull(4) ? null : secretProtector.Unprotect(TokenPurpose, reader.GetString(4)),
            IsEnabled: reader.GetInt32(5) == 1,
            HealthStatus: reader.GetString(6),
            LastHealthMessage: reader.IsDBNull(7) ? null : reader.GetString(7),
            LastHealthLatencyMs: reader.IsDBNull(8) ? null : reader.GetInt32(8),
            LastHealthTestUtc: reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)),
            ServerName: reader.IsDBNull(10) ? null : reader.GetString(10),
            ServerId: reader.IsDBNull(11) ? null : reader.GetString(11),
            CreatedUtc: DateTimeOffset.Parse(reader.GetString(12)),
            UpdatedUtc: DateTimeOffset.Parse(reader.GetString(13)))
        {
            LastHealthFailure = reader.IsDBNull(14) ? null : DeserializeFailure(reader.GetString(14))
        };

    private static IntegrationFailure? DeserializeFailure(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<IntegrationFailure>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
