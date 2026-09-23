using System.Text.Json;
using Deluno.Infrastructure.Storage;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Platform.Data;

/// <summary>
/// Download-folder entries a person chose to keep (#588), so an orphaned
/// folder they want is not counted as a problem again.
/// </summary>
public interface IKeptDownloadsRepository
{
    Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken);

    Task KeepAsync(string path, CancellationToken cancellationToken);

    Task ForgetAsync(string path, CancellationToken cancellationToken);
}

/// <summary>One settings row, read and rewritten whole, like the RSS marks next door.</summary>
public sealed class SqliteKeptDownloadsRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider)
    : IKeptDownloadsRepository
{
    private const string SettingKey = "downloads.orphans.kept.v1";

    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT setting_value FROM system_settings WHERE setting_key = @key;";
        AddParameter(command, "@key", SettingKey);
        var value = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(value) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public async Task KeepAsync(string path, CancellationToken cancellationToken)
    {
        var kept = (await ListAsync(cancellationToken)).ToList();
        if (!kept.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            kept.Add(path);
            await SaveAsync(kept, cancellationToken);
        }
    }

    public async Task ForgetAsync(string path, CancellationToken cancellationToken)
    {
        var kept = (await ListAsync(cancellationToken)).ToList();
        if (kept.RemoveAll(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            await SaveAsync(kept, cancellationToken);
        }
    }

    private async Task SaveAsync(IReadOnlyList<string> kept, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO system_settings (setting_key, setting_value, updated_utc)
            VALUES (@key, @value, @updatedUtc)
            ON CONFLICT(setting_key) DO UPDATE SET
                setting_value = excluded.setting_value,
                updated_utc = excluded.updated_utc;
            """;
        AddParameter(command, "@key", SettingKey);
        AddParameter(command, "@value", JsonSerializer.Serialize(kept));
        AddParameter(command, "@updatedUtc", timeProvider.GetUtcNow().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
