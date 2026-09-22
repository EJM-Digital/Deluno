using System.Globalization;
using System.Text;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Platform.Contracts;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Platform.Data;

public sealed class SqliteDownloadHealthRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider)
    : IDownloadHealthRepository
{
    // v2 since #560. Records written under the first strike rule counted
    // sightings rather than time, so their strikes and start times cannot be
    // trusted; V0066 removes the v1 key rather than carry them forward.
    private const string DownloadHealthRecordsSettingKey = "download-health.records.v2";
    private static readonly TimeSpan DownloadHealthRetention = TimeSpan.FromDays(90);

    public async Task<IReadOnlyList<DownloadHealthRecord>> RecordDownloadHealthObservationsAsync(
        string clientId,
        IReadOnlyCollection<string> listedQueueItemIds,
        IReadOnlyList<DownloadHealthObservation> observations,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId) || (observations.Count == 0 && listedQueueItemIds.Count == 0))
        {
            return [];
        }

        var now = timeProvider.GetUtcNow();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var settings = await ReadSettingsAsync(connection, cancellationToken);
        var records = ReadDownloadHealthRecords(settings).Where(record => now - record.LastObservedUtc <= DownloadHealthRetention).ToList();
        var touched = new List<DownloadHealthRecord>();

        // Listed now without a finding it had: that problem is over.
        var listed = listedQueueItemIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stillSeen = observations
            .Select(observation => $"{observation.QueueItemId}{observation.Kind}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ended = records.RemoveAll(record =>
            string.Equals(record.ClientId, clientId, StringComparison.OrdinalIgnoreCase) &&
            listed.Contains(record.QueueItemId) &&
            !stillSeen.Contains($"{record.QueueItemId}{record.Kind}"));

        foreach (var observation in observations)
        {
            if (!string.Equals(observation.ClientId, clientId, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(observation.QueueItemId) ||
                string.IsNullOrWhiteSpace(observation.ReleaseName) || string.IsNullOrWhiteSpace(observation.Kind))
            {
                continue;
            }

            var releaseKey = NormalizeDownloadReleaseKey(observation.ReleaseName);
            var index = records.FindIndex(record =>
                string.Equals(record.ClientId, observation.ClientId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(record.QueueItemId, observation.QueueItemId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(record.Kind, observation.Kind, StringComparison.OrdinalIgnoreCase));
            var record = index >= 0 ? records[index] : null;

            // Seen again after a gap longer than a strike, Deluno was not
            // watching in between and cannot say the problem lasted, so the
            // spell starts again. That gap is also what separates two downloads
            // of the same torrent: its infohash is the queue item id.
            var continuing = record is not null && now - record.LastObservedUtc <= DownloadHealthStrikes.Window;
            var since = continuing ? record!.FirstObservedUtc : now;
            var updated = new DownloadHealthRecord(
                observation.ClientId.Trim(), observation.QueueItemId.Trim(), observation.ReleaseName.Trim(), releaseKey,
                observation.Kind.Trim(), observation.Severity.Trim(), SanitizeDownloadHealthEvidence(observation.Evidence),
                since, now, DownloadHealthStrikes.For(since, now), record?.IgnoredUntilUtc);

            if (index >= 0) records[index] = updated; else records.Add(updated);
            touched.Add(updated);
        }

        if (touched.Count == 0 && ended == 0)
        {
            return [];
        }

        await UpsertSettingAsync(connection, transaction, DownloadHealthRecordsSettingKey, JsonSerializer.Serialize(records), now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return touched;
    }

    public async Task<IReadOnlyList<DownloadHealthRecord>> ListDownloadHealthRecordsAsync(int take, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        return ReadDownloadHealthRecords(await ReadSettingsAsync(connection, cancellationToken))
            .Where(record => now - record.LastObservedUtc <= DownloadHealthRetention)
            .OrderByDescending(record => record.LastObservedUtc)
            .Take(Math.Clamp(take, 1, 200))
            .ToArray();
    }

    public async Task<Page<DownloadHealthRecord>> ListDownloadHealthRecordsPageAsync(
        PageRequest request,
        CancellationToken cancellationToken)
    {
        var pageSize = request.BoundedPageSize;
        var token = DelunoPageToken.Decode(request.PageToken, 4);
        DateTimeOffset cursorObservedUtc = default;
        var hasCursor = token is { } cursor &&
            DateTimeOffset.TryParse(cursor[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out cursorObservedUtc);
        var now = timeProvider.GetUtcNow();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);

        var records = ReadDownloadHealthRecords(await ReadSettingsAsync(connection, cancellationToken))
            .Where(record => now - record.LastObservedUtc <= DownloadHealthRetention)
            .OrderByDescending(record => record.LastObservedUtc)
            .ThenBy(record => record.ClientId, StringComparer.Ordinal)
            .ThenBy(record => record.QueueItemId, StringComparer.Ordinal)
            .ThenBy(record => record.Kind, StringComparer.Ordinal)
            .Where(record => !hasCursor ||
                record.LastObservedUtc < cursorObservedUtc ||
                (record.LastObservedUtc == cursorObservedUtc && string.CompareOrdinal(record.ClientId, token![1]) > 0) ||
                (record.LastObservedUtc == cursorObservedUtc && string.Equals(record.ClientId, token![1], StringComparison.Ordinal) && string.CompareOrdinal(record.QueueItemId, token[2]) > 0) ||
                (record.LastObservedUtc == cursorObservedUtc && string.Equals(record.ClientId, token![1], StringComparison.Ordinal) && string.Equals(record.QueueItemId, token[2], StringComparison.Ordinal) && string.CompareOrdinal(record.Kind, token[3]) > 0))
            .Take(pageSize + 1)
            .ToList();

        var hasMore = records.Count > pageSize;
        if (hasMore) records.RemoveAt(records.Count - 1);
        var nextPageToken = hasMore
            ? DelunoPageToken.Encode(records[^1].LastObservedUtc.ToString("O"), records[^1].ClientId, records[^1].QueueItemId, records[^1].Kind)
            : null;
        return Page<DownloadHealthRecord>.Of(records, nextPageToken);
    }

    public async Task<DownloadHealthRecord?> IgnoreDownloadHealthFindingAsync(
        string clientId,
        string queueItemId,
        string kind,
        int durationDays,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var settings = await ReadSettingsAsync(connection, cancellationToken);
        var records = ReadDownloadHealthRecords(settings).ToList();
        var index = records.FindIndex(record =>
            string.Equals(record.ClientId, clientId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(record.QueueItemId, queueItemId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(record.Kind, kind, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;

        var updated = records[index] with { IgnoredUntilUtc = now.AddDays(Math.Clamp(durationDays, 1, 30)) };
        records[index] = updated;
        await UpsertSettingAsync(connection, transaction, DownloadHealthRecordsSettingKey, JsonSerializer.Serialize(records), now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadSettingsAsync(
        System.Data.Common.DbConnection connection,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT setting_key, setting_value FROM system_settings;";

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values[reader.GetString(0)] = reader.GetString(1);
        }

        return values;
    }

    private static IReadOnlyList<DownloadHealthRecord> ReadDownloadHealthRecords(IReadOnlyDictionary<string, string> settings)
    {
        if (!settings.TryGetValue(DownloadHealthRecordsSettingKey, out var json) || string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<DownloadHealthRecord>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string NormalizeDownloadReleaseKey(string releaseName)
    {
        var builder = new StringBuilder(releaseName.Length);
        foreach (var character in releaseName.Trim().ToUpperInvariant())
        {
            if (char.IsLetterOrDigit(character)) builder.Append(character);
        }
        return builder.ToString();
    }

    // Deluno is single-user and self-hosted: the import source path IS the
    // diagnostic the owner needs when an import fails, and nothing secret ever
    // appears in health evidence (paths and client messages only). Redacting
    // it forced failed-import diagnosis into the server logs (#248).
    private static string SanitizeDownloadHealthEvidence(string evidence)
        => evidence.Trim();

    private static async Task UpsertSettingAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string key,
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

        AddParameter(command, "@key", key);
        AddParameter(command, "@value", value);
        AddParameter(command, "@updatedUtc", updatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? GetValue(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out var value) ? value : null;
}
