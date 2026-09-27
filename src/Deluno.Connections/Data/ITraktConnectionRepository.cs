using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Security;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Connections.Data;

/// <summary>A stored token pair, decrypted for use — never logged, never returned from an API response.</summary>
public sealed record TraktTokenPair(string AccessToken, string RefreshToken, DateTimeOffset ExpiresUtc);

/// <summary>The registered Trakt app's own credentials, decrypted for use.</summary>
public sealed record TraktClientCredentials(string ClientId, string ClientSecret);

public interface ITraktConnectionRepository
{
    Task<TraktConnectionStatus> GetStatusAsync(CancellationToken cancellationToken);

    Task SetClientCredentialsAsync(string clientId, string clientSecret, CancellationToken cancellationToken);

    Task<TraktClientCredentials?> GetClientCredentialsAsync(CancellationToken cancellationToken);

    Task SaveTokensAsync(string accessToken, string refreshToken, DateTimeOffset expiresUtc, CancellationToken cancellationToken);

    Task<TraktTokenPair?> GetTokensAsync(CancellationToken cancellationToken);

    Task MarkNeedsReconnectAsync(CancellationToken cancellationToken);

    /// <summary>Clears the token pair, keeping the app's Client ID/Secret so reconnecting is one Connect click.</summary>
    Task DisconnectAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Deluno's single Trakt connection (#846) — one row, since Deluno is
/// single-user. Client ID/Secret and the token pair are all encrypted at
/// rest through the same <see cref="ISecretProtector"/> other credentials
/// use (see <c>SqliteSeerrRepository</c>, <c>metadata.tmdbApiKey</c>).
/// </summary>
public sealed class SqliteTraktConnectionRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider,
    ISecretProtector secretProtector)
    : ITraktConnectionRepository
{
    private const string RowId = "default";
    private const string ClientIdPurpose = "trakt:client-id";
    private const string ClientSecretPurpose = "trakt:client-secret";
    private const string AccessTokenPurpose = "trakt:access-token";
    private const string RefreshTokenPurpose = "trakt:refresh-token";

    public async Task<TraktConnectionStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var row = await ReadRowAsync(cancellationToken);
        if (row is null)
        {
            return new TraktConnectionStatus(ClientConfigured: false, Connected: false, ExpiresUtc: null, NeedsReconnect: false);
        }

        var clientConfigured = !string.IsNullOrWhiteSpace(row.Value.ClientId) && !string.IsNullOrWhiteSpace(row.Value.ClientSecret);
        var connected = !string.IsNullOrWhiteSpace(row.Value.AccessToken) && !string.IsNullOrWhiteSpace(row.Value.RefreshToken);
        return new TraktConnectionStatus(
            ClientConfigured: clientConfigured,
            Connected: connected,
            ExpiresUtc: row.Value.ExpiresUtc,
            NeedsReconnect: row.Value.NeedsReconnect);
    }

    public async Task SetClientCredentialsAsync(string clientId, string clientSecret, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO trakt_connection (id, client_id, client_secret, needs_reconnect, created_utc, updated_utc)
            VALUES (@id, @clientId, @clientSecret, 0, @now, @now)
            ON CONFLICT(id) DO UPDATE SET
                client_id = @clientId,
                client_secret = @clientSecret,
                updated_utc = @now;
            """;
        AddParameter(command, "@id", RowId);
        AddParameter(command, "@clientId", secretProtector.Protect(ClientIdPurpose, clientId.Trim()));
        AddParameter(command, "@clientSecret", secretProtector.Protect(ClientSecretPurpose, clientSecret.Trim()));
        AddParameter(command, "@now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<TraktClientCredentials?> GetClientCredentialsAsync(CancellationToken cancellationToken)
    {
        var row = await ReadRowAsync(cancellationToken);
        if (row is null || string.IsNullOrWhiteSpace(row.Value.ClientId) || string.IsNullOrWhiteSpace(row.Value.ClientSecret))
        {
            return null;
        }

        return new TraktClientCredentials(row.Value.ClientId, row.Value.ClientSecret);
    }

    public async Task SaveTokensAsync(string accessToken, string refreshToken, DateTimeOffset expiresUtc, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO trakt_connection (id, access_token, refresh_token, token_expires_utc, needs_reconnect, created_utc, updated_utc)
            VALUES (@id, @accessToken, @refreshToken, @expiresUtc, 0, @now, @now)
            ON CONFLICT(id) DO UPDATE SET
                access_token = @accessToken,
                refresh_token = @refreshToken,
                token_expires_utc = @expiresUtc,
                needs_reconnect = 0,
                updated_utc = @now;
            """;
        AddParameter(command, "@id", RowId);
        AddParameter(command, "@accessToken", secretProtector.Protect(AccessTokenPurpose, accessToken));
        AddParameter(command, "@refreshToken", secretProtector.Protect(RefreshTokenPurpose, refreshToken));
        AddParameter(command, "@expiresUtc", expiresUtc.ToString("O"));
        AddParameter(command, "@now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<TraktTokenPair?> GetTokensAsync(CancellationToken cancellationToken)
    {
        var row = await ReadRowAsync(cancellationToken);
        if (row is null || string.IsNullOrWhiteSpace(row.Value.AccessToken) || string.IsNullOrWhiteSpace(row.Value.RefreshToken) || row.Value.ExpiresUtc is null)
        {
            return null;
        }

        return new TraktTokenPair(row.Value.AccessToken, row.Value.RefreshToken, row.Value.ExpiresUtc.Value);
    }

    public async Task MarkNeedsReconnectAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE trakt_connection SET needs_reconnect = 1, updated_utc = @now WHERE id = @id;";
        AddParameter(command, "@id", RowId);
        AddParameter(command, "@now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE trakt_connection
            SET access_token = NULL, refresh_token = NULL, token_expires_utc = NULL, needs_reconnect = 0, updated_utc = @now
            WHERE id = @id;
            """;
        AddParameter(command, "@id", RowId);
        AddParameter(command, "@now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private readonly record struct Row(
        string? ClientId,
        string? ClientSecret,
        string? AccessToken,
        string? RefreshToken,
        DateTimeOffset? ExpiresUtc,
        bool NeedsReconnect);

    private async Task<Row?> ReadRowAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT client_id, client_secret, access_token, refresh_token, token_expires_utc, needs_reconnect FROM trakt_connection WHERE id = @id LIMIT 1;";
        AddParameter(command, "@id", RowId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new Row(
            ClientId: reader.IsDBNull(0) ? null : secretProtector.Unprotect(ClientIdPurpose, reader.GetString(0)),
            ClientSecret: reader.IsDBNull(1) ? null : secretProtector.Unprotect(ClientSecretPurpose, reader.GetString(1)),
            AccessToken: reader.IsDBNull(2) ? null : secretProtector.Unprotect(AccessTokenPurpose, reader.GetString(2)),
            RefreshToken: reader.IsDBNull(3) ? null : secretProtector.Unprotect(RefreshTokenPurpose, reader.GetString(3)),
            ExpiresUtc: reader.IsDBNull(4) ? null : ParseTimestamp(reader.GetString(4)),
            NeedsReconnect: !reader.IsDBNull(5) && reader.GetInt64(5) == 1);
    }
}
