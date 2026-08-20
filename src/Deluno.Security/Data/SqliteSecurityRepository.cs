using System.Globalization;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Deluno.Infrastructure.Storage;
using Deluno.Security.Contracts;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Security.Data;

/// <summary>
/// The users and API-keys slice of the Platform SQLite database. Split out of
/// SqlitePlatformSettingsRepository by ADR-001 Step 1, with method bodies
/// unchanged.
///
/// It opens <see cref="DelunoDatabaseNames.Platform"/>: splitting the C# does
/// not split the database file, so the users and api_keys tables stay under
/// the Platform migrations.
/// </summary>
public sealed class SqliteSecurityRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider)
    : ISecurityRepository
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> lastUsedWrites = new();
    // Pre-computed hash used to ensure constant-time response when a username is not found,
    // preventing timing-based username enumeration attacks.
    private static readonly string DummyPasswordHash =
        "100000.AAAAAAAAAAAAAAAAAAAAAA==.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    public async Task<bool> HasUsersAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        return await HasUsersAsync(connection, cancellationToken);
    }

    public async Task<bool> RequiresBootstrapAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        return await RequiresBootstrapAsync(connection, cancellationToken);
    }

    public async Task<UserItem?> ValidateUserCredentialsAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, username, display_name, password_hash, avatar_initials, security_stamp, created_utc
            FROM users
            WHERE username = @username COLLATE NOCASE
            LIMIT 1;
            """;
        AddParameter(command, "@username", NormalizeName(username));

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            VerifyPassword(password, DummyPasswordHash);
            return null;
        }

        var passwordHash = reader.GetString(3);
        if (!VerifyPassword(password, passwordHash))
        {
            return null;
        }

        return new UserItem(
            Id: reader.GetString(0),
            Username: reader.GetString(1),
            DisplayName: reader.GetString(2),
            AvatarInitials: reader.GetString(4),
            SecurityStamp: ReadSecurityStamp(reader, 5),
            CreatedUtc: ParseTimestamp(reader.GetString(6)));
    }

    public async Task<UserItem?> GetUserByIdAsync(
        string id,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, username, display_name, avatar_initials, security_stamp, created_utc
            FROM users
            WHERE id = @id
            LIMIT 1;
            """;
        AddParameter(command, "@id", id);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new UserItem(
            Id: reader.GetString(0),
            Username: reader.GetString(1),
            DisplayName: reader.GetString(2),
            AvatarInitials: reader.GetString(3),
            SecurityStamp: ReadSecurityStamp(reader, 4),
            CreatedUtc: ParseTimestamp(reader.GetString(5)));
    }

    public async Task<IReadOnlyList<ApiKeyItem>> ListApiKeysAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var items = new List<ApiKeyItem>();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, name, prefix, scopes, last_used_utc, created_utc, updated_utc
            FROM api_keys
            ORDER BY created_utc DESC;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadApiKey(reader));
        }

        return items;
    }

    public async Task<CreatedApiKeyResponse> CreateApiKeyAsync(
        CreateApiKeyRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var now = timeProvider.GetUtcNow();
        var rawKey = GenerateApiKey();
        var prefix = BuildApiKeyPrefix(rawKey);
        var item = new ApiKeyItem(
            Guid.CreateVersion7().ToString("N"),
            NormalizeName(request.Name) ?? "API key",
            prefix,
            NormalizeApiScopes(request.Scopes),
            null,
            now,
            now);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO api_keys (
                id, name, key_hash, prefix, scopes, last_used_utc, created_utc, updated_utc
            )
            VALUES (
                @id, @name, @keyHash, @prefix, @scopes, NULL, @createdUtc, @updatedUtc
            );
            """;
        AddParameter(command, "@id", item.Id);
        AddParameter(command, "@name", item.Name);
        AddParameter(command, "@keyHash", HashApiKey(rawKey));
        AddParameter(command, "@prefix", item.Prefix);
        AddParameter(command, "@scopes", item.Scopes);
        AddParameter(command, "@createdUtc", now.ToString("O"));
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return new CreatedApiKeyResponse(item, rawKey);
    }

    public async Task<ApiKeyItem?> ValidateApiKeyAsync(
        string apiKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var keyHash = HashApiKey(apiKey.Trim());
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, name, prefix, scopes, last_used_utc, created_utc, updated_utc
            FROM api_keys
            WHERE key_hash = @keyHash
            LIMIT 1;
            """;
        AddParameter(command, "@keyHash", keyHash);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var item = ReadApiKey(reader);
        await reader.DisposeAsync();

        var now = timeProvider.GetUtcNow();
        var due = !lastUsedWrites.TryGetValue(item.Id, out var previous) ||
                  now - previous >= TimeSpan.FromMinutes(1);
        if (!due)
        {
            return item;
        }

        lastUsedWrites[item.Id] = now;

        using var update = connection.CreateCommand();
        update.CommandText =
            """
            UPDATE api_keys
            SET last_used_utc = @lastUsedUtc,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;
        AddParameter(update, "@id", item.Id);
        AddParameter(update, "@lastUsedUtc", now.ToString("O"));
        AddParameter(update, "@updatedUtc", now.ToString("O"));
        await update.ExecuteNonQueryAsync(cancellationToken);

        return item with
        {
            LastUsedUtc = now,
            UpdatedUtc = now
        };
    }

    public async Task<bool> DeleteApiKeyAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM api_keys WHERE id = @id;";
        AddParameter(command, "@id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> ChangeUserPasswordAsync(
        string userId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var readCommand = connection.CreateCommand();
        readCommand.CommandText =
            """
            SELECT password_hash
            FROM users
            WHERE id = @id
            LIMIT 1;
            """;
        AddParameter(readCommand, "@id", userId);

        var existing = await readCommand.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(existing) || !VerifyPassword(currentPassword, existing))
        {
            return false;
        }

        using var updateCommand = connection.CreateCommand();
        updateCommand.CommandText =
            """
            UPDATE users
            SET password_hash = @passwordHash,
                security_stamp = @securityStamp,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;
        AddParameter(updateCommand, "@id", userId);
        AddParameter(updateCommand, "@passwordHash", HashPassword(newPassword));
        AddParameter(updateCommand, "@securityStamp", CreateSecurityStamp());
        AddParameter(updateCommand, "@updatedUtc", timeProvider.GetUtcNow().ToString("O"));

        return await updateCommand.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> RevokeUserAccessTokensAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE users
            SET security_stamp = @securityStamp,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;
        AddParameter(command, "@id", userId);
        AddParameter(command, "@securityStamp", CreateSecurityStamp());
        AddParameter(command, "@updatedUtc", timeProvider.GetUtcNow().ToString("O"));

        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<UserItem> BootstrapUserAsync(
        BootstrapUserRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        if (await HasUsersAsync(connection, cancellationToken))
        {
            throw new InvalidOperationException("Deluno has already been configured.");
        }

        var now = timeProvider.GetUtcNow();
        var username = NormalizeName(request.Username) ?? "user";
        var displayName = NormalizeName(request.DisplayName) ?? username;
        var item = new UserItem(
            Id: Guid.CreateVersion7().ToString("N"),
            Username: username,
            DisplayName: displayName,
            AvatarInitials: BuildAvatarInitials(displayName),
            SecurityStamp: CreateSecurityStamp(),
            CreatedUtc: now);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO users (
                id, username, display_name, password_hash, avatar_initials, security_stamp, created_utc, updated_utc
            )
            VALUES (
                @id, @username, @displayName, @passwordHash, @avatarInitials, @securityStamp, @createdUtc, @updatedUtc
            );
            """;

        AddParameter(command, "@id", item.Id);
        AddParameter(command, "@username", item.Username);
        AddParameter(command, "@displayName", item.DisplayName);
        AddParameter(command, "@passwordHash", HashPassword(request.Password ?? string.Empty));
        AddParameter(command, "@avatarInitials", item.AvatarInitials);
        AddParameter(command, "@securityStamp", item.SecurityStamp);
        AddParameter(command, "@createdUtc", item.CreatedUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return item;
    }

    private static async Task<bool> HasUsersAsync(
        System.Data.Common.DbConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM users;";
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(scalar ?? 0, CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<bool> RequiresBootstrapAsync(
        System.Data.Common.DbConnection connection,
        CancellationToken cancellationToken)
    {
        return !await HasUsersAsync(connection, cancellationToken);
    }

    private static async Task<UserItem?> GetUserAsync(
        System.Data.Common.DbConnection connection,
        string id,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, username, display_name, avatar_initials, security_stamp, created_utc
            FROM users
            WHERE id = @id
            LIMIT 1;
            """;
        AddParameter(command, "@id", id);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadUser(reader) : null;
    }

    private static string BuildAvatarInitials(string value)
    {
        var parts = value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(2)
            .Select(part => char.ToUpperInvariant(part[0]))
            .ToArray();
        return parts.Length == 0 ? "OP" : new string(parts);
    }

    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        const int iterations = 100_000;
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        return $"{iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split('.', 3);
        if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var iterations))
        {
            return false;
        }

        var salt = Convert.FromBase64String(parts[1]);
        var expected = Convert.FromBase64String(parts[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static string GenerateApiKey()
        => $"deluno_{Base64UrlEncode(RandomNumberGenerator.GetBytes(32))}";

    private static string CreateSecurityStamp()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static string BuildApiKeyPrefix(string apiKey)
    {
        var value = apiKey.Trim();
        return value.Length <= 18 ? value : $"{value[..14]}...";
    }

    private static string HashApiKey(string apiKey)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey.Trim())));

    private static string NormalizeApiScopes(string? value)
    {
        var normalized = NormalizeCsv(value);
        return string.IsNullOrWhiteSpace(normalized) ? "all" : normalized;
    }

    private static string Base64UrlEncode(byte[] value)
        => Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static UserItem ReadUser(System.Data.Common.DbDataReader reader)
    {
        return new UserItem(
            Id: reader.GetString(0),
            Username: reader.GetString(1),
            DisplayName: reader.GetString(2),
            AvatarInitials: reader.GetString(3),
            SecurityStamp: ReadSecurityStamp(reader, 4),
            CreatedUtc: ParseTimestamp(reader.GetString(5)));
    }

    private static string ReadSecurityStamp(System.Data.Common.DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) || string.IsNullOrWhiteSpace(reader.GetString(ordinal))
            ? CreateSecurityStamp()
            : reader.GetString(ordinal);

    private static ApiKeyItem ReadApiKey(System.Data.Common.DbDataReader reader)
    {
        return new ApiKeyItem(
            Id: reader.GetString(0),
            Name: reader.GetString(1),
            Prefix: reader.GetString(2),
            Scopes: reader.GetString(3),
            LastUsedUtc: reader.IsDBNull(4) ? null : ParseTimestamp(reader.GetString(4)),
            CreatedUtc: ParseTimestamp(reader.GetString(5)),
            UpdatedUtc: ParseTimestamp(reader.GetString(6)));
    }

}
