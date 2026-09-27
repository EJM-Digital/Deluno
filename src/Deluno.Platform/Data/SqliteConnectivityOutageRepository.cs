using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Platform.Contracts;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Platform.Data;

/// <summary>
/// Persists outage windows the same way <see cref="SqliteDownloadHealthRepository"/>
/// persists health records: one JSON blob under a key in system_settings.
/// Retention matches that repository's 90 days, since a download health
/// record's <c>FirstObservedUtc</c> can be that old and still needs an
/// outage from back then to be subtractable (#901).
/// </summary>
public sealed class SqliteConnectivityOutageRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider)
    : IConnectivityOutageRepository
{
    private const string OutageWindowsSettingKey = "connectivity.outage-windows.v1";
    private static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    public async Task<IReadOnlyList<ConnectivityOutageWindow>> ListWindowsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        var json = await ReadSettingAsync(connection, cancellationToken);
        return TrimExpired(ReadWindows(json), timeProvider.GetUtcNow());
    }

    public async Task RecordOutageStartedAsync(DateTimeOffset startUtc, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var windows = TrimExpired(ReadWindows(await ReadSettingAsync(connection, cancellationToken)), startUtc).ToList();

        // Never two windows open at once: an already-open window absorbs this
        // call instead of the outage silently starting a second one.
        if (windows.Count == 0 || windows[^1].EndUtc is not null)
        {
            windows.Add(new ConnectivityOutageWindow(startUtc, null));
            await UpsertSettingAsync(connection, transaction, JsonSerializer.Serialize(windows), startUtc, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
    }

    public async Task RecordOutageEndedAsync(DateTimeOffset endUtc, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var windows = TrimExpired(ReadWindows(await ReadSettingAsync(connection, cancellationToken)), endUtc).ToList();
        if (windows.Count == 0 || windows[^1].EndUtc is not null)
        {
            return;
        }

        windows[^1] = windows[^1] with { EndUtc = endUtc };
        await UpsertSettingAsync(connection, transaction, JsonSerializer.Serialize(windows), endUtc, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static IReadOnlyList<ConnectivityOutageWindow> TrimExpired(IReadOnlyList<ConnectivityOutageWindow> windows, DateTimeOffset now)
        => windows.Where(window => window.EndUtc is null || now - window.EndUtc.Value <= Retention).ToArray();

    private static IReadOnlyList<ConnectivityOutageWindow> ReadWindows(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ConnectivityOutageWindow>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static async Task<string?> ReadSettingAsync(System.Data.Common.DbConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT setting_value FROM system_settings WHERE setting_key = @key;";
        AddParameter(command, "@key", OutageWindowsSettingKey);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result as string;
    }

    private static async Task UpsertSettingAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string value,
        DateTimeOffset updatedUtc,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO system_settings (setting_key, setting_value, updated_utc)
            VALUES (@key, @value, @updatedUtc)
            ON CONFLICT(setting_key) DO UPDATE SET
                setting_value = excluded.setting_value,
                updated_utc = excluded.updated_utc;
            """;

        AddParameter(command, "@key", OutageWindowsSettingKey);
        AddParameter(command, "@value", value);
        AddParameter(command, "@updatedUtc", updatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
