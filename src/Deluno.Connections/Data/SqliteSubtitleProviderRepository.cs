using System.Data.Common;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
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
            consecutive_failures, rate_limited_until_utc, disabled_reason, created_utc, updated_utc,
            last_health_failure_json
        """;

    public async Task<IReadOnlyList<SubtitleProviderConnection>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        await ReactivateDueProvidersAsync(connection, cancellationToken);

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
    /// Turns a paused provider back on once the reason it was paused for has
    /// passed on its own (#946).
    ///
    /// <para>Only a timed pause qualifies: <c>rate_limited_until_utc</c> set
    /// and in the past. A wrong-login pause (#896) never sets that column —
    /// <see cref="RecordHealthAsync"/> clears it for that kind on purpose —
    /// so it stays off until a person fixes the credential and turns it back
    /// on themselves. Run before every list, so a used-up quota provider is
    /// asked again the moment its allowance would have reset, not only after
    /// somebody happens to open the settings screen.</para>
    /// </summary>
    private async Task ReactivateDueProvidersAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE subtitle_providers
            SET is_enabled = 1,
                disabled_reason = NULL,
                rate_limited_until_utc = NULL
            WHERE is_enabled = 0
              AND rate_limited_until_utc IS NOT NULL
              AND rate_limited_until_utc <= @now;
            """;
        AddParameter(command, "@now", timeProvider.GetUtcNow().ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
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
                -- A person switching a provider off by hand is a deliberate,
                -- fresh choice; any reset time left over from an earlier
                -- quota pause must not later flip it back on behind them
                -- (ReactivateDueProvidersAsync only ever acts on that
                -- column, so this is the one place it needs clearing).
                rate_limited_until_utc = CASE WHEN excluded.is_enabled = 0 THEN NULL ELSE subtitle_providers.rate_limited_until_utc END,
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
    ///
    /// <para><b>A wrong login or a used-up quota actually pauses the provider
    /// (#896, #946).</b> Before this, the row recorded the failure and Deluno
    /// kept asking it every cycle regardless — wasting a search attempt on a
    /// source that cannot answer. <c>is_enabled</c> is turned off and
    /// <c>disabled_reason</c> is filled in, the same field the screen already
    /// had a column for. Off is what
    /// <see cref="SubtitleProviderConnection.IsAskable"/> already reads, so
    /// this is the one write that makes "search again: no" true — no second
    /// check anywhere else needs to know why.</para>
    ///
    /// <para><b>The two pauses come back differently.</b> A wrong login needs
    /// a person to fix the credential, so its pause never sets
    /// <c>rate_limited_until_utc</c> — it stays off until a manual save turns
    /// it back on. A used-up quota resets on its own, so its pause does set
    /// that column to when the allowance resets; <see cref="ListAsync"/>
    /// reads it back off automatically once that time passes
    /// (<see cref="ReactivateDueProvidersAsync"/>), same as turning it back on
    /// by hand would.</para>
    /// </summary>
    public async Task RecordHealthAsync(
        string providerKey,
        string status,
        string? message,
        int? latencyMs,
        bool success,
        DateTimeOffset? rateLimitedUntilUtc,
        CancellationToken cancellationToken,
        IntegrationFailure? failure = null)
    {
        var now = timeProvider.GetUtcNow();
        var isAuthFailure = !success && failure?.Kind == IntegrationFailureKind.Authentication;
        var isQuotaFailure = !success && failure?.Kind == IntegrationFailureKind.QuotaExceeded;
        var pausesProvider = isAuthFailure || isQuotaFailure;

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
                rate_limited_until_utc = CASE WHEN @isAuthFailure = 1 THEN NULL ELSE COALESCE(@rateLimitedUntil, rate_limited_until_utc) END,
                last_health_failure_json = @failureJson,
                is_enabled = CASE WHEN @pausesProvider = 1 THEN 0 ELSE is_enabled END,
                disabled_reason = CASE WHEN @success = 1 THEN NULL WHEN @pausesProvider = 1 THEN @message ELSE disabled_reason END,
                updated_utc = @updatedUtc
            WHERE provider_key = @providerKey COLLATE NOCASE;
            """;

        AddParameter(command, "@status", status);
        AddParameter(command, "@message", message);
        AddParameter(command, "@latency", latencyMs);
        AddParameter(command, "@testedUtc", now.ToString("O"));
        AddParameter(command, "@success", success ? 1 : 0);
        AddParameter(command, "@rateLimitedUntil", rateLimitedUntilUtc?.ToString("O"));
        AddParameter(command, "@failureJson", failure is null ? null : JsonSerializer.Serialize(failure));
        AddParameter(command, "@isAuthFailure", isAuthFailure ? 1 : 0);
        AddParameter(command, "@pausesProvider", pausesProvider ? 1 : 0);
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
            UpdatedUtc: DateTimeOffset.Parse(reader.GetString(16)))
        {
            LastHealthFailure = reader.IsDBNull(17) ? null : DeserializeFailure(reader.GetString(17))
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
