using System.Globalization;
using System.Text;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Contracts.Connectivity;
using Deluno.Infrastructure.Storage;
using Deluno.Platform.Contracts;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Platform.Data;

public sealed class SqliteDownloadHealthRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider,
    IConnectivityMonitor? connectivityMonitor = null)
    : IDownloadHealthRepository
{
    // Tests, and any caller that predates #901, construct this without a
    // monitor. "Never seen an outage" is the correct default: strikes accrue
    // exactly as they always have until something actually wires one up.
    private readonly IConnectivityMonitor _connectivityMonitor = connectivityMonitor ?? NullConnectivityMonitor.Instance;

    // v2 since #560. Records written under the first strike rule counted
    // sightings rather than time, so their strikes and start times cannot be
    // trusted; V0066 removes the v1 key rather than carry them forward.
    private const string DownloadHealthRecordsSettingKey = "download-health.records.v2";
    private const string DownloadProgressRecordsSettingKey = "download-health.progress.v1";
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
            // #901: strikes are time, and outage time is not held against a
            // download. FirstObservedUtc itself is left true to when the
            // problem was actually first seen (evidence still reads right);
            // only the elapsed span the strike count is built from skips
            // whatever part of it was an outage rather than a bad release.
            var outageOverlap = await _connectivityMonitor.OutageOverlapAsync(since, now, cancellationToken);
            var updated = new DownloadHealthRecord(
                observation.ClientId.Trim(), observation.QueueItemId.Trim(), observation.ReleaseName.Trim(), releaseKey,
                observation.Kind.Trim(), observation.Severity.Trim(), SanitizeDownloadHealthEvidence(observation.Evidence),
                since, now, DownloadHealthStrikes.For(since + outageOverlap, now), record?.IgnoredUntilUtc);

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

    public async Task<int> ForgetDownloadClientAsync(string clientId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return 0;
        var now = timeProvider.GetUtcNow();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var settings = await ReadSettingsAsync(connection, cancellationToken);
        var records = ReadDownloadHealthRecords(settings).ToList();
        var forgotten = records.RemoveAll(record => string.Equals(record.ClientId, clientId, StringComparison.OrdinalIgnoreCase));

        // Its progress memory is the same kind of leftover #568 was filed
        // about: a deleted client is never polled again, so nothing would
        // otherwise clear it either.
        var progressRecords = ReadDownloadProgressRecords(settings).ToList();
        var forgottenProgress = progressRecords.RemoveAll(record => string.Equals(record.ClientId, clientId, StringComparison.OrdinalIgnoreCase));

        if (forgotten == 0 && forgottenProgress == 0) return 0;

        if (forgotten > 0)
        {
            await UpsertSettingAsync(connection, transaction, DownloadHealthRecordsSettingKey, JsonSerializer.Serialize(records), now, cancellationToken);
        }
        if (forgottenProgress > 0)
        {
            await UpsertSettingAsync(connection, transaction, DownloadProgressRecordsSettingKey, JsonSerializer.Serialize(progressRecords), now, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return forgotten + forgottenProgress;
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

    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> RecordDownloadProgressAsync(
        string clientId,
        IReadOnlyList<(string QueueItemId, long DownloadedBytes)> observations,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(clientId) || observations.Count == 0)
        {
            return result;
        }

        var now = timeProvider.GetUtcNow();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var settings = await ReadSettingsAsync(connection, cancellationToken);
        var records = ReadDownloadProgressRecords(settings)
            .Where(record => now - record.LastObservedUtc <= DownloadHealthRetention)
            .ToList();

        foreach (var (queueItemId, downloadedBytes) in observations)
        {
            if (string.IsNullOrWhiteSpace(queueItemId))
            {
                continue;
            }

            var trimmedId = queueItemId.Trim();
            var index = records.FindIndex(record =>
                string.Equals(record.ClientId, clientId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(record.QueueItemId, trimmedId, StringComparison.OrdinalIgnoreCase));
            var existing = index >= 0 ? records[index] : null;

            // No prior record: this is the first sighting, not a stall - the
            // clock starts now, the same way a freshly-added magnet is not
            // stalled the instant it is seen. Otherwise only an actual gain in
            // bytes moves the clock; equal or fewer bytes (a paused torrent, a
            // recheck) leaves it exactly where it was.
            var lastIncreaseUtc = existing is null || downloadedBytes > existing.DownloadedBytes
                ? now
                : existing.LastIncreaseUtc;
            var updated = new DownloadProgressRecord(clientId.Trim(), trimmedId, downloadedBytes, lastIncreaseUtc, now);
            if (index >= 0) records[index] = updated; else records.Add(updated);
            result[trimmedId] = lastIncreaseUtc;
        }

        await UpsertSettingAsync(connection, transaction, DownloadProgressRecordsSettingKey, JsonSerializer.Serialize(records), now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static IReadOnlyList<DownloadProgressRecord> ReadDownloadProgressRecords(IReadOnlyDictionary<string, string> settings)
    {
        if (!settings.TryGetValue(DownloadProgressRecordsSettingKey, out var json) || string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<DownloadProgressRecord>>(json) ?? []; }
        catch (JsonException) { return []; }
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
