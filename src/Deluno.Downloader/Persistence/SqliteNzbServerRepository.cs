using System.Globalization;
using Deluno.Downloader.Nzb.Nntp;
using Deluno.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Deluno.Downloader.Persistence;

/// <summary>
/// SQLite-backed <see cref="INzbServerRepository"/>. Credentials are
/// stored as <see cref="IDownloaderSecretProtector"/>-protected
/// strings (TEXT columns) and round-tripped to plaintext for callers.
/// Empty / null credentials are stored as DBNull and surfaced as
/// <c>null</c>.
/// </summary>
public sealed class SqliteNzbServerRepository(
    IDelunoDatabaseConnectionFactory connectionFactory,
    IDownloaderSecretProtector secrets)
    : INzbServerRepository
{
    private const string DbName = DelunoDatabaseNames.Downloader;
    private const string UsernamePurpose = "nzb-server.username";
    private const string PasswordPurpose = "nzb-server.password";
    private const string ProxyPurpose    = "nzb-server.proxy-url";

    public async Task<IReadOnlyList<NntpServerOptions>> ListEnabledAsync(CancellationToken ct)
    {
        await using var conn = await connectionFactory.OpenConnectionAsync(DbName, ct);
        await using var cmd = (SqliteCommand)conn.CreateCommand();
        cmd.CommandText = $"SELECT {AllColumns} FROM nzb_servers WHERE enabled = 1 ORDER BY tier ASC, priority ASC;";
        var results = new List<NntpServerOptions>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) results.Add(MapServer(reader, secrets));
        return results;
    }

    public async Task<NntpServerOptions?> GetAsync(string id, CancellationToken ct)
    {
        await using var conn = await connectionFactory.OpenConnectionAsync(DbName, ct);
        await using var cmd = (SqliteCommand)conn.CreateCommand();
        cmd.CommandText = $"SELECT {AllColumns} FROM nzb_servers WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapServer(reader, secrets) : null;
    }

    public async Task UpsertAsync(NntpServerOptions server, CancellationToken ct)
    {
        await using var conn = await connectionFactory.OpenConnectionAsync(DbName, ct);
        await using var cmd = (SqliteCommand)conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO nzb_servers (
                id, name, host, port, use_tls,
                username_protected, password_protected,
                max_connections, priority, tier, retention_days, enabled,
                proxy_url_protected, cert_pin_sha256,
                created_at, updated_at
            ) VALUES (
                $id, $name, $host, $port, $use_tls,
                $user_p, $pass_p,
                $max_conn, $priority, $tier, $retention, $enabled,
                $proxy_p, NULL,
                $now, $now
            )
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                host = excluded.host,
                port = excluded.port,
                use_tls = excluded.use_tls,
                username_protected = excluded.username_protected,
                password_protected = excluded.password_protected,
                max_connections = excluded.max_connections,
                priority = excluded.priority,
                tier = excluded.tier,
                retention_days = excluded.retention_days,
                enabled = excluded.enabled,
                proxy_url_protected = excluded.proxy_url_protected,
                updated_at = excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$id", server.Id);
        cmd.Parameters.AddWithValue("$name", server.Name);
        cmd.Parameters.AddWithValue("$host", server.Host);
        cmd.Parameters.AddWithValue("$port", server.Port);
        cmd.Parameters.AddWithValue("$use_tls", server.UseTls ? 1 : 0);
        cmd.Parameters.AddWithValue("$user_p", ProtectOrNull(server.Username, UsernamePurpose));
        cmd.Parameters.AddWithValue("$pass_p", ProtectOrNull(server.Password, PasswordPurpose));
        cmd.Parameters.AddWithValue("$max_conn", server.MaxConnections);
        cmd.Parameters.AddWithValue("$priority", server.Priority);
        cmd.Parameters.AddWithValue("$tier", server.Tier.ToString());
        cmd.Parameters.AddWithValue("$retention", (object?)server.RetentionDays ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled", server.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$proxy_p", DBNull.Value); // proxy plumbing not wired through NntpServerOptions yet
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RemoveAsync(string id, CancellationToken ct)
    {
        await using var conn = await connectionFactory.OpenConnectionAsync(DbName, ct);
        await using var cmd = (SqliteCommand)conn.CreateCommand();
        cmd.CommandText = "DELETE FROM nzb_servers WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private const string AllColumns =
        "id, name, host, port, use_tls, username_protected, password_protected, " +
        "max_connections, priority, tier, retention_days, enabled, proxy_url_protected";

    private static NntpServerOptions MapServer(SqliteDataReader r, IDownloaderSecretProtector secrets)
    {
        var id = r.GetString(0);
        var name = r.GetString(1);
        var host = r.GetString(2);
        var port = r.GetInt32(3);
        var useTls = r.GetInt64(4) != 0;
        var username = secrets.Unprotect(UsernamePurpose, r.IsDBNull(5) ? null : r.GetString(5));
        var password = secrets.Unprotect(PasswordPurpose, r.IsDBNull(6) ? null : r.GetString(6));
        var maxConn = r.GetInt32(7);
        var priority = r.GetInt32(8);
        var tier = Enum.Parse<NntpServerTier>(r.GetString(9));
        int? retention = r.IsDBNull(10) ? null : r.GetInt32(10);
        var enabled = r.GetInt64(11) != 0;
        // proxy_url_protected at column 12 — currently ignored; surfaces when NntpServerOptions adds a Proxy field.
        return new NntpServerOptions(
            Id: id,
            Name: name,
            Host: host,
            Port: port,
            UseTls: useTls,
            Username: string.IsNullOrEmpty(username) ? null : username,
            Password: string.IsNullOrEmpty(password) ? null : password,
            MaxConnections: maxConn,
            Priority: priority,
            Tier: tier,
            RetentionDays: retention,
            Enabled: enabled);
    }

    private object ProtectOrNull(string? plaintext, string purpose)
    {
        if (string.IsNullOrEmpty(plaintext)) return DBNull.Value;
        return secrets.Protect(purpose, plaintext);
    }
}
