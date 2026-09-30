using System.Data.Common;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Security;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;
using static Deluno.Contracts.DelunoValueNormalizers;

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

    /// <summary>Every mapping, or one server's, oldest/broadest last (#662).</summary>
    Task<IReadOnlyList<MediaServerPathMappingItem>> ListPathMappingsAsync(string? mediaServerId, CancellationToken cancellationToken);

    Task<MediaServerPathMappingItem?> CreatePathMappingAsync(
        string mediaServerId,
        CreateMediaServerPathMappingRequest request,
        CancellationToken cancellationToken);

    Task<bool> DeletePathMappingAsync(string mediaServerId, string id, CancellationToken cancellationToken);
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
            last_health_failure_json, leaving_soon_on_home
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
              INSERT INTO media_servers (id, kind, name, base_url, token, is_enabled, health_status, created_utc, updated_utc, leaving_soon_on_home)
              VALUES (@id, @kind, @name, @baseUrl, @token, @isEnabled, 'untested', @now, @now, COALESCE(@onHome, 0));
              """
            : """
              UPDATE media_servers
              SET kind = @kind,
                  name = @name,
                  base_url = @baseUrl,
                  token = @token,
                  is_enabled = @isEnabled,
                  leaving_soon_on_home = COALESCE(@onHome, leaving_soon_on_home),
                  health_status = CASE WHEN @addressChanged = 1 THEN 'untested' ELSE health_status END,
                  server_name = CASE WHEN @addressChanged = 1 THEN NULL ELSE server_name END,
                  server_id = CASE WHEN @addressChanged = 1 THEN NULL ELSE server_id END,
                  updated_utc = @now
              WHERE id = @id;
              """;
        var rowId = existing?.Id ?? Guid.CreateVersion7().ToString("N");
        AddParameter(command, "@id", rowId);
        AddParameter(command, "@kind", MediaServerKinds.Normalize(request.Kind) ?? existing?.Kind);
        AddParameter(command, "@name", ConnectionNames.Unique(
            ConnectionNames.AppOnHost(MediaServerKinds.DisplayName(MediaServerKinds.Normalize(request.Kind) ?? existing?.Kind ?? string.Empty), request.BaseUrl),
            ConnectionNames.PortOf(request.BaseUrl), rowId,
            await ConnectionNameRows.OtherNamesAsync(connection, "media_servers", rowId, cancellationToken)));
        AddParameter(command, "@baseUrl", NormalizeBaseUrl(request.BaseUrl));
        AddParameter(command, "@token", string.IsNullOrWhiteSpace(token) ? null : secretProtector.Protect(TokenPurpose, token));
        AddParameter(command, "@isEnabled", request.IsEnabled ? 1 : 0);
        AddParameter(command, "@onHome", request.LeavingSoonOnHome is null ? null : request.LeavingSoonOnHome.Value ? 1 : 0);
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

    public async Task<IReadOnlyList<MediaServerPathMappingItem>> ListPathMappingsAsync(string? mediaServerId, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, media_server_id, deluno_path, server_path, is_enabled, priority, created_utc, updated_utc
            FROM media_server_path_mappings
            WHERE @mediaServerId IS NULL OR media_server_id = @mediaServerId
            ORDER BY priority ASC, LENGTH(deluno_path) DESC, deluno_path ASC;
            """;
        AddParameter(command, "@mediaServerId", string.IsNullOrWhiteSpace(mediaServerId) ? null : mediaServerId.Trim());

        var items = new List<MediaServerPathMappingItem>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new MediaServerPathMappingItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4) == 1,
                reader.GetInt32(5),
                ParseTimestamp(reader.GetString(6)),
                ParseTimestamp(reader.GetString(7))));
        }

        return items;
    }

    public async Task<MediaServerPathMappingItem?> CreatePathMappingAsync(
        string mediaServerId,
        CreateMediaServerPathMappingRequest request,
        CancellationToken cancellationToken)
    {
        var server = await GetAsync(mediaServerId, cancellationToken);
        if (server is null) return null;

        var delunoPath = NormalizePath(request.DelunoPath);
        var serverPath = NormalizePath(request.ServerPath);
        if (string.IsNullOrWhiteSpace(delunoPath) || string.IsNullOrWhiteSpace(serverPath)) return null;

        var now = timeProvider.GetUtcNow();
        var item = new MediaServerPathMappingItem(
            Guid.CreateVersion7().ToString("N"),
            server.Id,
            delunoPath,
            serverPath,
            request.IsEnabled,
            request.Priority is >= 1 ? request.Priority.Value : 10,
            now,
            now);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO media_server_path_mappings (
                id, media_server_id, deluno_path, server_path, is_enabled, priority, created_utc, updated_utc)
            VALUES (@id, @mediaServerId, @delunoPath, @serverPath, @isEnabled, @priority, @createdUtc, @updatedUtc);
            """;
        AddParameter(command, "@id", item.Id);
        AddParameter(command, "@mediaServerId", item.MediaServerId);
        AddParameter(command, "@delunoPath", item.DelunoPath);
        AddParameter(command, "@serverPath", item.ServerPath);
        AddParameter(command, "@isEnabled", item.IsEnabled ? 1 : 0);
        AddParameter(command, "@priority", item.Priority);
        AddParameter(command, "@createdUtc", item.CreatedUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", item.UpdatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return item;
    }

    public async Task<bool> DeletePathMappingAsync(string mediaServerId, string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM media_server_path_mappings WHERE id = @id AND media_server_id = @mediaServerId;";
        AddParameter(command, "@id", id);
        AddParameter(command, "@mediaServerId", mediaServerId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public static string NormalizeBaseUrl(string? value)
        => (value ?? string.Empty).Trim().TrimEnd('/');

    private MediaServerConnection Read(DbDataReader reader)
    {
        var token = reader.IsDBNull(4) ? SecretReadResult.None : secretProtector.TryUnprotect(TokenPurpose, reader.GetString(4));
        var problem = token.Unreadable ? SecretUnreadable.Message("access token") : null;
        var enabled = reader.GetInt32(5) == 1;
        return new(
            Id: reader.GetString(0),
            Kind: reader.GetString(1),
            Name: reader.GetString(2),
            BaseUrl: reader.GetString(3),
            Token: token.Value,
            IsEnabled: enabled,
            HealthStatus: problem is not null && enabled ? "degraded" : reader.GetString(6),
            LastHealthMessage: problem ?? (reader.IsDBNull(7) ? null : reader.GetString(7)),
            LastHealthLatencyMs: reader.IsDBNull(8) ? null : reader.GetInt32(8),
            LastHealthTestUtc: reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)),
            ServerName: reader.IsDBNull(10) ? null : reader.GetString(10),
            ServerId: reader.IsDBNull(11) ? null : reader.GetString(11),
            CreatedUtc: DateTimeOffset.Parse(reader.GetString(12)),
            UpdatedUtc: DateTimeOffset.Parse(reader.GetString(13)))
        {
            LastHealthFailure = reader.IsDBNull(14) ? null : DeserializeFailure(reader.GetString(14)),
            LeavingSoonOnHome = reader.GetInt32(15) == 1,
            SecretUnreadable = problem is not null,
            SecretProblem = problem
        };
    }

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
