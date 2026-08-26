using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Platform.Contracts;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Platform.Data;

/// <summary>
/// Stores the sharing picture as one settings row rather than a table of its
/// own (#288).
///
/// It is a snapshot that is wholly rewritten every pass and never queried by
/// anything but "give me all of it", so a table would buy nothing and cost a
/// schema migration. The download-health records next door are kept the same
/// way for the same reason.
/// </summary>
public sealed class SqliteDownloadSharingRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider)
    : IDownloadSharingRepository
{
    private const string SharingStateSettingKey = "download-sharing.state.v1";

    /// <summary>
    /// How long a snapshot is worth showing. The pass runs every thirty
    /// seconds, so anything this old means the worker is not running — and a
    /// dashboard confidently reporting yesterday's disk usage is worse than one
    /// that says nothing.
    /// </summary>
    private static readonly TimeSpan SnapshotFreshness = TimeSpan.FromMinutes(10);

    public async Task ReplaceHoldsAsync(
        IReadOnlyList<DownloadSharingHold> holds,
        string? driveNote,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var snapshot = new DownloadSharingSnapshot(
            holds,
            holds.Where(hold => !hold.SharesLibraryCopy).Sum(hold => Math.Max(0, hold.SizeBytes)),
            string.IsNullOrWhiteSpace(driveNote) ? null : driveNote,
            now);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

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

        AddParameter(command, "@key", SharingStateSettingKey);
        AddParameter(command, "@value", JsonSerializer.Serialize(snapshot));
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<DownloadSharingSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT setting_value FROM system_settings WHERE setting_key = @key;";
        AddParameter(command, "@key", SharingStateSettingKey);

        var value = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(value))
        {
            return DownloadSharingSnapshot.Empty;
        }

        DownloadSharingSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<DownloadSharingSnapshot>(value);
        }
        catch (JsonException)
        {
            return DownloadSharingSnapshot.Empty;
        }

        if (snapshot is null)
        {
            return DownloadSharingSnapshot.Empty;
        }

        return snapshot.ObservedUtc is { } observed && timeProvider.GetUtcNow() - observed > SnapshotFreshness
            ? DownloadSharingSnapshot.Empty
            : snapshot with { Holds = snapshot.Holds ?? [] };
    }
}
