using System.Data.Common;
using Deluno.Connections.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Security;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Connections.Data;

/// <summary>
/// The configured subtitle sources.
///
/// <para>Its own repository rather than fourteen more methods on
/// <see cref="SqliteConnectionsRepository"/>, which ADR-001 Step 1 has just
/// finished splitting for being too large. Same database, same secret protector,
/// same health vocabulary.</para>
///
/// <para>The purpose labels handed to <c>ISecretProtector</c> —
/// <c>subtitle-provider:secret</c> and <c>subtitle-provider:api-key</c> — are
/// cryptographic labels, not names. Changing either makes every already-stored
/// credential undecryptable, which is why the indexer's says so too.</para>
/// </summary>
public sealed class SqliteSubtitleProviderRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider,
    ISecretProtector secretProtector)
    : ISubtitleProviderRepository
{
    private const string SecretPurpose = "subtitle-provider:secret";
    private const string ApiKeyPurpose = "subtitle-provider:api-key";

    private const string Columns =
        """
            id, provider_key, name, username, secret, api_key, priority, is_enabled,
            health_status, last_health_message, last_health_latency_ms, last_health_test_utc,
            consecutive_failures, rate_limited_until_utc, disabled_reason, created_utc, updated_utc
        """;

    public async Task<IReadOnlyList<SubtitleProviderConnection>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT
            {Columns}
            FROM subtitle_providers
            ORDER BY priority ASC, name ASC;
            """;

        var items = new List<SubtitleProviderConnection>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(Read(reader));
        }

        return items;
    }

    /// <summary>
    /// Saves one provider's settings, creating the row the first time.
    ///
    /// <para>An upsert on the provider key rather than an id, because there is
    /// exactly one of each: the client is code Deluno ships, so "add another
    /// OpenSubtitles" is not a thing a person can want. The unique index would
    /// refuse it anyway; this makes the API honest about it instead of returning
    /// a constraint error.</para>
    ///
    /// <para><b>A blank secret keeps the stored one.</b> The screen cannot show
    /// what is saved, so it sends blank when it was not touched — and treating
    /// that as "clear it" would wipe an account every time somebody changed the
    /// priority. Clearing is done by disabling the provider or by sending the
    /// literal word the endpoint documents.</para>
    /// </summary>
    public async Task<SubtitleProviderConnection> SaveAsync(
        string providerKey,
        string displayName,
        SaveSubtitleProviderRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var existing = (await ListAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase));

        var secret = string.IsNullOrWhiteSpace(request.Secret) ? existing?.Secret : request.Secret.Trim();
        var apiKey = string.IsNullOrWhiteSpace(request.ApiKey) ? existing?.ApiKey : request.ApiKey.Trim();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO subtitle_providers (
                id, provider_key, name, username, secret, api_key, priority, is_enabled,
                health_status, created_utc, updated_utc
            )
            VALUES (
                @id, @providerKey, @name, @username, @secret, @apiKey, @priority, @isEnabled,
                @healthStatus, @createdUtc, @updatedUtc
            )
            ON CONFLICT (provider_key) DO UPDATE SET
                name = excluded.name,
                username = excluded.username,
                secret = excluded.secret,
                api_key = excluded.api_key,
                priority = excluded.priority,
                is_enabled = excluded.is_enabled,
                updated_utc = excluded.updated_utc;
            """;

        AddParameter(command, "@id", existing?.Id ?? Guid.CreateVersion7().ToString("N"));
        AddParameter(command, "@providerKey", providerKey);
        AddParameter(command, "@name", displayName);
        AddParameter(command, "@username", string.IsNullOrWhiteSpace(request.Username) ? null : request.Username.Trim());
        AddParameter(command, "@secret", string.IsNullOrWhiteSpace(secret) ? null : secretProtector.Protect(SecretPurpose, secret));
        AddParameter(command, "@apiKey", string.IsNullOrWhiteSpace(apiKey) ? null : secretProtector.Protect(ApiKeyPurpose, apiKey));
        AddParameter(command, "@priority", request.Priority ?? existing?.Priority ?? 100);
        AddParameter(command, "@isEnabled", request.IsEnabled ? 1 : 0);
        AddParameter(command, "@healthStatus", existing?.HealthStatus ?? "untested");
        AddParameter(command, "@createdUtc", (existing?.CreatedUtc ?? now).ToString("O"));
        AddParameter(command, "@updatedUtc", now.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);

        return (await ListAsync(cancellationToken))
            .First(item => string.Equals(item.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// What a test or a real search found out about this source.
    ///
    /// <para>The same shape an indexer's health update takes: a status, a
    /// sentence, how long it took, and the consecutive-failure count that decides
    /// whether "needs you" says anything. A success resets the count — a source
    /// that failed twice last week and works now is a working source, and a
    /// counter that only goes up would eventually disable everything.</para>
    /// </summary>
    public async Task RecordHealthAsync(
        string providerKey,
        string status,
        string? message,
        int? latencyMs,
        bool success,
        DateTimeOffset? rateLimitedUntilUtc,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE subtitle_providers
            SET health_status = @status,
                last_health_message = @message,
                last_health_latency_ms = @latency,
                last_health_test_utc = @testedUtc,
                consecutive_failures = CASE WHEN @success = 1 THEN 0 ELSE consecutive_failures + 1 END,
                rate_limited_until_utc = COALESCE(@rateLimitedUntil, rate_limited_until_utc),
                updated_utc = @updatedUtc
            WHERE provider_key = @providerKey COLLATE NOCASE;
            """;

        AddParameter(command, "@status", status);
        AddParameter(command, "@message", message);
        AddParameter(command, "@latency", latencyMs);
        AddParameter(command, "@testedUtc", now.ToString("O"));
        AddParameter(command, "@success", success ? 1 : 0);
        AddParameter(command, "@rateLimitedUntil", rateLimitedUntilUtc?.ToString("O"));
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        AddParameter(command, "@providerKey", providerKey);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(string providerKey, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM subtitle_providers WHERE provider_key = @providerKey COLLATE NOCASE;";
        AddParameter(command, "@providerKey", providerKey);

        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private SubtitleProviderConnection Read(DbDataReader reader)
        => new(
            Id: reader.GetString(0),
            ProviderKey: reader.GetString(1),
            Name: reader.GetString(2),
            Username: reader.IsDBNull(3) ? null : reader.GetString(3),
            Secret: reader.IsDBNull(4) ? null : secretProtector.Unprotect(SecretPurpose, reader.GetString(4)),
            ApiKey: reader.IsDBNull(5) ? null : secretProtector.Unprotect(ApiKeyPurpose, reader.GetString(5)),
            Priority: reader.GetInt32(6),
            IsEnabled: reader.GetInt32(7) == 1,
            HealthStatus: reader.GetString(8),
            LastHealthMessage: reader.IsDBNull(9) ? null : reader.GetString(9),
            LastHealthLatencyMs: reader.IsDBNull(10) ? null : reader.GetInt32(10),
            LastHealthTestUtc: reader.IsDBNull(11) ? null : DateTimeOffset.Parse(reader.GetString(11)),
            ConsecutiveFailures: reader.GetInt32(12),
            RateLimitedUntilUtc: reader.IsDBNull(13) ? null : DateTimeOffset.Parse(reader.GetString(13)),
            DisabledReason: reader.IsDBNull(14) ? null : reader.GetString(14),
            CreatedUtc: DateTimeOffset.Parse(reader.GetString(15)),
            UpdatedUtc: DateTimeOffset.Parse(reader.GetString(16)));
}
