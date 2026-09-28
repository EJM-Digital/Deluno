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

    /// <summary>
    /// Generates a fresh webhook secret, stores it (encrypted, like the API
    /// key) and returns the plaintext once so the Seerr card can show and
    /// copy it into Overseerr's Authorization header field (#851). Replaces
    /// any secret already set, so an old Overseerr webhook config stops
    /// working the moment a new one is generated.
    /// </summary>
    Task<string?> RegenerateWebhookSecretAsync(string id, CancellationToken cancellationToken);
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
    private const string WebhookSecretPurpose = "seerr:webhook-secret";

    // #1033: movie_4k_library_id/series_4k_library_id and their profile and
    // fallback-switch columns stay in the table (an old migration added
    // them) but are no longer read here — a one-time migration turned any
    // set mapping into a "requested in 4K" destination rule, and library
    // routing reads that instead. See V0121SeerrRequestsUseLibraryRoutingRules.
    private const string Columns =
        "id, name, base_url, api_key, is_enabled, health_status, last_health_message, last_health_latency_ms, last_health_test_utc, server_name, version, created_utc, updated_utc, movie_library_id, movie_quality_profile_id, series_library_id, series_quality_profile_id, webhook_secret, tags, tags_4k";

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
              INSERT INTO seerr_servers (
                  id, name, base_url, api_key, is_enabled, health_status, created_utc, updated_utc,
                  movie_library_id, movie_quality_profile_id, series_library_id, series_quality_profile_id,
                  tags, tags_4k
              )
              VALUES (
                  @id, @name, @baseUrl, @apiKey, @isEnabled, 'untested', @now, @now,
                  @movieLibraryId, @movieQualityProfileId, @seriesLibraryId, @seriesQualityProfileId,
                  @tags, @tags4k
              );
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
                  movie_library_id = @movieLibraryId,
                  movie_quality_profile_id = @movieQualityProfileId,
                  series_library_id = @seriesLibraryId,
                  series_quality_profile_id = @seriesQualityProfileId,
                  tags = @tags,
                  tags_4k = @tags4k,
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
        AddParameter(command, "@movieLibraryId", string.IsNullOrWhiteSpace(request.MovieLibraryId) ? null : request.MovieLibraryId.Trim());
        AddParameter(command, "@movieQualityProfileId", string.IsNullOrWhiteSpace(request.MovieQualityProfileId) ? null : request.MovieQualityProfileId.Trim());
        AddParameter(command, "@seriesLibraryId", string.IsNullOrWhiteSpace(request.SeriesLibraryId) ? null : request.SeriesLibraryId.Trim());
        AddParameter(command, "@seriesQualityProfileId", string.IsNullOrWhiteSpace(request.SeriesQualityProfileId) ? null : request.SeriesQualityProfileId.Trim());
        AddParameter(command, "@tags", request.Tags is null ? existing?.Tags ?? string.Empty : NormalizeCsv(request.Tags));
        AddParameter(command, "@tags4k", request.Tags4k is null ? existing?.Tags4k ?? string.Empty : NormalizeCsv(request.Tags4k));
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

    public async Task<string?> RegenerateWebhookSecretAsync(string id, CancellationToken cancellationToken)
    {
        var existing = await GetAsync(id, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        var secret = GenerateWebhookSecret();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE seerr_servers SET webhook_secret = @secret, updated_utc = @now WHERE id = @id;";
        AddParameter(command, "@id", id);
        AddParameter(command, "@secret", secretProtector.Protect(WebhookSecretPurpose, secret));
        AddParameter(command, "@now", timeProvider.GetUtcNow().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return secret;
    }

    private static string GenerateWebhookSecret()
        => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

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
            UpdatedUtc: DateTimeOffset.Parse(reader.GetString(12)),
            MovieLibraryId: reader.IsDBNull(13) ? null : reader.GetString(13),
            MovieQualityProfileId: reader.IsDBNull(14) ? null : reader.GetString(14),
            SeriesLibraryId: reader.IsDBNull(15) ? null : reader.GetString(15),
            SeriesQualityProfileId: reader.IsDBNull(16) ? null : reader.GetString(16),
            WebhookSecret: reader.IsDBNull(17) ? null : secretProtector.Unprotect(WebhookSecretPurpose, reader.GetString(17)),
            Tags: reader.GetString(18),
            Tags4k: reader.GetString(19));
}
