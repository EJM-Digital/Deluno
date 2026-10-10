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
    IConnectivityMonitor? connectivityMonitor = null,
    IRealtimeEventPublisher? realtimeEventPublisher = null)
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
    private const string DownloadHealthRulesSettingKey = "download-health.rules.v1";
    private const string DownloadHealthIgnoreListSettingKey = "download-health.ignore-list.v1";
    private const string ContentBlocklistSettingsSettingKey = "download-health.content-blocklist.v1";
    private const string QbittorrentBlocklistSyncStatesSettingKey = "download-health.content-blocklist.qbit-sync.v1";
    private static readonly TimeSpan DownloadHealthRetention = TimeSpan.FromDays(90);

    public async Task<IReadOnlyList<DownloadHealthRecord>> RecordDownloadHealthObservationsAsync(
        string clientId,
        IReadOnlyCollection<string> listedQueueItemIds,
        IReadOnlyList<DownloadHealthObservation> observations,
        TimeSpan defaultStrikeWindow,
        TimeSpan strikeInactivityWindow,
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
            .Select(observation => $"{observation.QueueItemId}{observation.Kind}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ended = records.RemoveAll(record =>
            string.Equals(record.ClientId, clientId, StringComparison.OrdinalIgnoreCase) &&
            listed.Contains(record.QueueItemId) &&
            !stillSeen.Contains($"{record.QueueItemId}{record.Kind}"));

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

            // #825: a gap longer than the (independently configurable) strike
            // inactivity window means Deluno was not watching closely enough
            // to say the problem lasted, so the spell starts again - the same
            // as two downloads of the same torrent sharing one queue item id.
            // A shorter gap keeps the spell going; strikes still accrue at
            // the rule's own (usually much tighter) timer, not this window.
            var continuing = record is not null && now - record.LastObservedUtc <= strikeInactivityWindow;
            var since = continuing ? record!.FirstObservedUtc : now;
            // One check interval for every rule (#1194): a strike per check, not per elapsed time.
            var window = defaultStrikeWindow;
            // #901: strikes are time, and outage time is not held against a
            // download. FirstObservedUtc itself is left true to when the
            // problem was actually first seen (evidence still reads right);
            // only the elapsed span the strike count is built from skips
            // whatever part of it was an outage rather than a bad release.
            var lastStrike = continuing ? record!.LastStrikeUtc ?? record.FirstObservedUtc : now;
            var outageOverlap = continuing
                ? await _connectivityMonitor.OutageOverlapAsync(lastStrike, now, cancellationToken)
                : TimeSpan.Zero;
            var counts = DownloadHealthStrikes.Counts(observation.Kind);
            var previousStrikes = continuing ? record!.StrikeCount : 0;
            var due = counts && continuing && previousStrikes > 0 &&
                DownloadHealthStrikes.IsDueForStrike(lastStrike, now, window, outageOverlap);
            var strikes = !counts ? 0
                : !continuing || previousStrikes == 0 ? 1
                : due ? DownloadHealthStrikes.CapAtLimit(previousStrikes + 1, observation.RuleStrikeLimit)
                : previousStrikes;
            // A strike is booked at its scheduled moment, so the spacing stays one "Check every" interval (#1198).
            var struckAt = due && strikes != previousStrikes
                ? DownloadHealthStrikes.StrikeLandedAt(lastStrike, now, window, outageOverlap)
                : counts && strikes != previousStrikes || !continuing ? now : lastStrike;
            var updated = new DownloadHealthRecord(
                observation.ClientId.Trim(), observation.QueueItemId.Trim(), observation.ReleaseName.Trim(), releaseKey,
                observation.Kind.Trim(), observation.Severity.Trim(), SanitizeDownloadHealthEvidence(observation.Evidence),
                since, now,
                // Only a problem with getting the bytes earns strikes (#560, #1154). A failed hand-off or import
                // on a finished download is shown, but it is not struck, so it never reads "53 strikes so far".
                strikes,
                record?.IgnoredUntilUtc,
                observation.RuleId, observation.RuleStrikeLimit, observation.RuleTimerMinutes, observation.RuleActionPreset,
                counts ? struckAt : null);

            if (index >= 0) records[index] = updated; else records.Add(updated);
            touched.Add(updated);
        }

        if (touched.Count == 0 && ended == 0)
        {
            return [];
        }

        await UpsertSettingAsync(connection, transaction, DownloadHealthRecordsSettingKey, JsonSerializer.Serialize(records), now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await AnnounceChangedAsync(clientId, cancellationToken);
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
        if (forgotten > 0) await AnnounceChangedAsync(clientId, cancellationToken);
        return forgotten + forgottenProgress;
    }

    public async Task<int> MoveDownloadHealthItemIdAsync(
        string clientId,
        string oldQueueItemId,
        string newQueueItemId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(oldQueueItemId) || string.IsNullOrWhiteSpace(newQueueItemId)
            || string.Equals(oldQueueItemId, newQueueItemId, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var now = timeProvider.GetUtcNow();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var settings = await ReadSettingsAsync(connection, cancellationToken);
        var moved = 0;

        bool Ours(string recordClient, string recordItem, string id)
            => string.Equals(recordClient, clientId, StringComparison.OrdinalIgnoreCase)
               && string.Equals(recordItem, id, StringComparison.OrdinalIgnoreCase);

        var records = ReadDownloadHealthRecords(settings).ToList();
        var rewritten = new List<DownloadHealthRecord>(records.Count);
        foreach (var record in records)
        {
            if (!Ours(record.ClientId, record.QueueItemId, oldQueueItemId))
            {
                rewritten.Add(record);
                continue;
            }

            moved++;
            var alreadyThere = records.Any(other => Ours(other.ClientId, other.QueueItemId, newQueueItemId)
                && string.Equals(other.Kind, record.Kind, StringComparison.OrdinalIgnoreCase));
            if (!alreadyThere)
            {
                rewritten.Add(record with { QueueItemId = newQueueItemId });
            }
        }

        var progress = ReadDownloadProgressRecords(settings).ToList();
        var progressRewritten = new List<DownloadProgressRecord>(progress.Count);
        var progressMoved = 0;
        foreach (var record in progress)
        {
            if (!Ours(record.ClientId, record.QueueItemId, oldQueueItemId))
            {
                progressRewritten.Add(record);
                continue;
            }

            progressMoved++;
            if (!progress.Any(other => Ours(other.ClientId, other.QueueItemId, newQueueItemId)))
            {
                progressRewritten.Add(record with { QueueItemId = newQueueItemId });
            }
        }

        if (moved == 0 && progressMoved == 0)
        {
            return 0;
        }

        if (moved > 0)
        {
            await UpsertSettingAsync(connection, transaction, DownloadHealthRecordsSettingKey, JsonSerializer.Serialize(rewritten), now, cancellationToken);
        }

        if (progressMoved > 0)
        {
            await UpsertSettingAsync(connection, transaction, DownloadProgressRecordsSettingKey, JsonSerializer.Serialize(progressRewritten), now, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        if (moved > 0) await AnnounceChangedAsync(clientId, cancellationToken);
        return moved + progressMoved;
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
        await AnnounceChangedAsync(clientId, cancellationToken);
        return updated;
    }

    public async Task<int> PurgeAllDownloadHealthStrikesAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var settings = await ReadSettingsAsync(connection, cancellationToken);
        var records = ReadDownloadHealthRecords(settings);
        if (records.Count == 0) return 0;

        await UpsertSettingAsync(connection, transaction, DownloadHealthRecordsSettingKey, JsonSerializer.Serialize(Array.Empty<DownloadHealthRecord>()), now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await AnnounceChangedAsync(null, cancellationToken);
        return records.Count;
    }

    public async Task<int> PruneDownloadHealthHistoryAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var settings = await ReadSettingsAsync(connection, cancellationToken);

        var records = ReadDownloadHealthRecords(settings).ToList();
        var removedRecords = records.RemoveAll(record => record.LastObservedUtc < olderThan);

        var progressRecords = ReadDownloadProgressRecords(settings).ToList();
        var removedProgress = progressRecords.RemoveAll(record => record.LastObservedUtc < olderThan);

        if (removedRecords == 0 && removedProgress == 0) return 0;

        if (removedRecords > 0)
        {
            await UpsertSettingAsync(connection, transaction, DownloadHealthRecordsSettingKey, JsonSerializer.Serialize(records), now, cancellationToken);
        }
        if (removedProgress > 0)
        {
            await UpsertSettingAsync(connection, transaction, DownloadProgressRecordsSettingKey, JsonSerializer.Serialize(progressRecords), now, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        if (removedRecords > 0) await AnnounceChangedAsync(null, cancellationToken);
        return removedRecords + removedProgress;
    }

    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> RecordDownloadProgressAsync(
        string clientId,
        IReadOnlyList<(string QueueItemId, long DownloadedBytes)> observations,
        CancellationToken cancellationToken,
        bool allowReset = true,
        long minimumGainBytes = 0)
        => (await ObserveDownloadProgressAsync(clientId, observations, cancellationToken, allowReset, minimumGainBytes))
            .ToDictionary(pair => pair.Key, pair => pair.Value.LastIncreaseUtc, StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyDictionary<string, DownloadProgressObservation>> ObserveDownloadProgressAsync(
        string clientId,
        IReadOnlyList<(string QueueItemId, long DownloadedBytes)> observations,
        CancellationToken cancellationToken,
        bool allowReset = true,
        long minimumGainBytes = 0)
    {
        var result = new Dictionary<string, DownloadProgressObservation>(StringComparer.OrdinalIgnoreCase);
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
            //
            // #951: once a prior record exists, a rule with "reset strikes on
            // progress" turned off (allowReset false) never lets a further
            // gain move the clock again - the spell keeps accruing strikes
            // through a trickle instead of restarting every time a byte
            // arrives. A rule with a configured minimum (minimumGainBytes)
            // requires a gain at least that large to count; a smaller trickle
            // is not "real" progress for that rule's purposes.
            var gain = existing is null ? long.MaxValue : downloadedBytes - existing.DownloadedBytes;
            var isRealProgress = existing is null || (allowReset && gain >= Math.Max(1, minimumGainBytes));
            var lastIncreaseUtc = isRealProgress ? now : existing!.LastIncreaseUtc;
            var updated = new DownloadProgressRecord(clientId.Trim(), trimmedId, downloadedBytes, lastIncreaseUtc, now);
            if (index >= 0) records[index] = updated; else records.Add(updated);
            // #1207: "grew" is bytes against the previous check, whatever the reported speed was.
            result[trimmedId] = new DownloadProgressObservation(
                lastIncreaseUtc,
                existing is null ? null : downloadedBytes - existing.DownloadedBytes >= Math.Max(1, minimumGainBytes));
        }

        await UpsertSettingAsync(connection, transaction, DownloadProgressRecordsSettingKey, JsonSerializer.Serialize(records), now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<DownloadHealthRule>> GetDownloadHealthRulesAsync(
        PlatformSettingsSnapshot legacySettings,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        var settings = await ReadSettingsAsync(connection, cancellationToken);
        if (settings.TryGetValue(DownloadHealthRulesSettingKey, out var json) && !string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var stored = JsonSerializer.Deserialize<List<DownloadHealthRule>>(json);
                if (stored is not null) return stored;
            }
            catch (JsonException)
            {
                // Fall through to a fresh migration rather than fail the request.
            }
        }

        // First time this installation reads its rules (#905): synthesize the
        // equivalent of its existing flat toggles so nothing changes until it
        // opens the new screen, then persist that as the starting point.
        var migrated = LegacyRulesFrom(legacySettings, now);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await UpsertSettingAsync(connection, transaction, DownloadHealthRulesSettingKey, JsonSerializer.Serialize(migrated), now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return migrated;
    }

    public async Task<(IReadOnlyList<DownloadHealthRule> Rules, string? Error)> SaveDownloadHealthRuleAsync(
        DownloadHealthRule rule,
        PlatformSettingsSnapshot legacySettings,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var existing = (await GetDownloadHealthRulesAsync(legacySettings, cancellationToken)).ToList();
        var others = existing.Where(candidate => !string.Equals(candidate.Id, rule.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
        var error = DownloadHealthRuleValidator.Validate(rule, others);
        if (error is not null)
        {
            return (existing, error);
        }

        var index = existing.FindIndex(candidate => string.Equals(candidate.Id, rule.Id, StringComparison.OrdinalIgnoreCase));
        var saved = rule with
        {
            CreatedUtc = index >= 0 ? existing[index].CreatedUtc : now,
            UpdatedUtc = now
        };
        if (index >= 0) existing[index] = saved; else existing.Add(saved);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await UpsertSettingAsync(connection, transaction, DownloadHealthRulesSettingKey, JsonSerializer.Serialize(existing), now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (existing, null);
    }

    public async Task<IReadOnlyList<DownloadHealthRule>> DeleteDownloadHealthRuleAsync(
        string ruleId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        var settings = await ReadSettingsAsync(connection, cancellationToken);
        var existing = settings.TryGetValue(DownloadHealthRulesSettingKey, out var json) && !string.IsNullOrWhiteSpace(json)
            ? SafeDeserialize<DownloadHealthRule>(json)
            : [];
        var remaining = existing.Where(rule => !string.Equals(rule.Id, ruleId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (remaining.Length == existing.Count) return existing;

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await UpsertSettingAsync(connection, transaction, DownloadHealthRulesSettingKey, JsonSerializer.Serialize(remaining), now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return remaining;
    }

    public async Task<IReadOnlyList<DownloadHealthIgnoreEntry>> ListDownloadHealthIgnoreEntriesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        var settings = await ReadSettingsAsync(connection, cancellationToken);
        return settings.TryGetValue(DownloadHealthIgnoreListSettingKey, out var json) && !string.IsNullOrWhiteSpace(json)
            ? SafeDeserialize<DownloadHealthIgnoreEntry>(json)
            : [];
    }

    public async Task<IReadOnlyList<DownloadHealthIgnoreEntry>> AddDownloadHealthIgnoreEntryAsync(
        DownloadHealthIgnoreEntry entry,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var settings = await ReadSettingsAsync(connection, cancellationToken);
        var existing = settings.TryGetValue(DownloadHealthIgnoreListSettingKey, out var json) && !string.IsNullOrWhiteSpace(json)
            ? SafeDeserialize<DownloadHealthIgnoreEntry>(json).ToList()
            : [];
        existing.Add(entry with { CreatedUtc = now });
        await UpsertSettingAsync(connection, transaction, DownloadHealthIgnoreListSettingKey, JsonSerializer.Serialize(existing), now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return existing;
    }

    public async Task<IReadOnlyList<DownloadHealthIgnoreEntry>> RemoveDownloadHealthIgnoreEntryAsync(
        string entryId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        var settings = await ReadSettingsAsync(connection, cancellationToken);
        var existing = settings.TryGetValue(DownloadHealthIgnoreListSettingKey, out var json) && !string.IsNullOrWhiteSpace(json)
            ? SafeDeserialize<DownloadHealthIgnoreEntry>(json)
            : [];
        var remaining = existing.Where(entry => !string.Equals(entry.Id, entryId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (remaining.Length == existing.Count) return existing;

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await UpsertSettingAsync(connection, transaction, DownloadHealthIgnoreListSettingKey, JsonSerializer.Serialize(remaining), now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return remaining;
    }

    public async Task<ContentBlocklistSettings> GetContentBlocklistSettingsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        var settings = await ReadSettingsAsync(connection, cancellationToken);
        return ReadContentBlocklistSettings(settings);
    }

    public async Task<ContentBlocklistSettings> SaveContentBlocklistSettingsAsync(
        ContentBlocklistSettings settings,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        var current = ReadContentBlocklistSettings(await ReadSettingsAsync(connection, cancellationToken));
        // A save from Settings never touches what the last remote refresh
        // fetched, or when it ran - only #822's own refresh job (below)
        // updates those, so a person editing the mode or custom patterns
        // never loses the last-known remote content.
        var saved = settings with
        {
            RemotePatterns = current.RemotePatterns,
            LastRefreshedUtc = current.LastRefreshedUtc,
            UpdatedUtc = now
        };
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await UpsertSettingAsync(connection, transaction, ContentBlocklistSettingsSettingKey, JsonSerializer.Serialize(saved), now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return saved;
    }

    public async Task<ContentBlocklistSettings> RecordContentBlocklistRemoteRefreshAsync(
        IReadOnlyList<string> remotePatterns,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        var current = ReadContentBlocklistSettings(await ReadSettingsAsync(connection, cancellationToken));
        var saved = current with { RemotePatterns = remotePatterns, LastRefreshedUtc = now, UpdatedUtc = now };
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await UpsertSettingAsync(connection, transaction, ContentBlocklistSettingsSettingKey, JsonSerializer.Serialize(saved), now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return saved;
    }

    private static ContentBlocklistSettings ReadContentBlocklistSettings(IReadOnlyDictionary<string, string> settings)
    {
        if (!settings.TryGetValue(ContentBlocklistSettingsSettingKey, out var json) || string.IsNullOrWhiteSpace(json))
        {
            return ContentBlocklistSettings.Default;
        }

        try
        {
            return JsonSerializer.Deserialize<ContentBlocklistSettings>(json) ?? ContentBlocklistSettings.Default;
        }
        catch (JsonException)
        {
            return ContentBlocklistSettings.Default;
        }
    }

    public async Task<IReadOnlyDictionary<string, QbittorrentBlocklistSyncState>> ListQbittorrentBlocklistSyncStatesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        var settings = await ReadSettingsAsync(connection, cancellationToken);
        return ReadQbittorrentBlocklistSyncStates(settings).ToDictionary(state => state.ClientId, StringComparer.OrdinalIgnoreCase);
    }

    public async Task RecordQbittorrentBlocklistSyncAsync(
        string clientId,
        string blocklistHash,
        bool succeeded,
        string? message,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return;
        var now = timeProvider.GetUtcNow();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        var settings = await ReadSettingsAsync(connection, cancellationToken);
        var existing = ReadQbittorrentBlocklistSyncStates(settings).ToList();
        var index = existing.FindIndex(state => string.Equals(state.ClientId, clientId, StringComparison.OrdinalIgnoreCase));
        var updated = new QbittorrentBlocklistSyncState(clientId, blocklistHash, succeeded, message, now);
        if (index >= 0) existing[index] = updated; else existing.Add(updated);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await UpsertSettingAsync(connection, transaction, QbittorrentBlocklistSyncStatesSettingKey, JsonSerializer.Serialize(existing), now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static IReadOnlyList<QbittorrentBlocklistSyncState> ReadQbittorrentBlocklistSyncStates(IReadOnlyDictionary<string, string> settings)
    {
        if (!settings.TryGetValue(QbittorrentBlocklistSyncStatesSettingKey, out var json) || string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<QbittorrentBlocklistSyncState>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    /// <summary>
    /// #905's own equivalent of every flat toggle Deluno shipped before it:
    /// one rule per kind, "dead torrent" off (it has no legacy toggle - #818
    /// is new), the rest carrying over their old enabled state, threshold and
    /// timer exactly. The shared strike threshold and cleanup-action booleans
    /// become each rule's own <see cref="DownloadHealthRule.StrikeLimit"/> and
    /// <see cref="DownloadHealthRule.ActionPreset"/>, identical across every
    /// migrated rule since the old system had only one of each for all kinds.
    /// </summary>
    private static IReadOnlyList<DownloadHealthRule> LegacyRulesFrom(PlatformSettingsSnapshot legacy, DateTimeOffset now)
    {
        var strikeLimit = Math.Clamp(legacy.DownloadHealthStrikeThreshold, 1, 100);
        var actionPreset = LegacyActionPreset(legacy);

        DownloadHealthRule Rule(string id, string kind, bool enabled, int timerMinutes, double threshold) => new(
            Id: id,
            Kind: kind,
            Enabled: enabled,
            StrikeLimit: strikeLimit,
            TimerMinutes: Math.Clamp(timerMinutes, 1, 1440),
            AppliesTo: DownloadHealthAppliesTo.Both,
            MinCompletionPercent: 0,
            MaxCompletionPercent: 100,
            ResetStrikesOnProgress: true,
            ActionPreset: actionPreset,
            CreatedUtc: now,
            UpdatedUtc: now,
            Threshold: threshold);

        return
        [
            Rule("legacy-no-progress", DownloadHealthRuleKinds.NoProgress, legacy.DownloadHealthNoProgressEnabled, legacy.DownloadHealthNoProgressMinutes, 0),
            Rule("legacy-too-slow", DownloadHealthRuleKinds.TooSlow, legacy.DownloadHealthMinSpeedEnabled, legacy.DownloadHealthMinSpeedMinutes, legacy.DownloadHealthMinSpeedKbps),
            Rule("legacy-low-peers", DownloadHealthRuleKinds.LowPeers, legacy.DownloadHealthMinPeersEnabled, legacy.DownloadHealthMinPeersMinutes, legacy.DownloadHealthMinPeers),
            // #818: the timer here is the strike-repeat window, not the grace
            // period - the grace period is the rule's own Threshold, which
            // DownloadHealthEvaluator reads for DeadTorrent.
            Rule("legacy-dead-torrent", DownloadHealthRuleKinds.DeadTorrent, legacy.DownloadHealthDeadTorrentEnabled, 30, legacy.DownloadHealthDeadTorrentGraceMinutes),
            // #879: one confirmed dangerous file is enough - a 1-minute timer
            // and a strike limit of 1 (Cleanuparr's "instant") rather than the
            // shared legacy strikeLimit/timer, regardless of what those are
            // set to for the timed rules above.
            Rule("legacy-dangerous-file", DownloadHealthRuleKinds.DangerousFile, legacy.DownloadHealthDangerousFileEnabled, 1, 0) with { StrikeLimit = 1 }
        ];
    }

    private static string LegacyActionPreset(PlatformSettingsSnapshot legacy)
    {
        if (legacy.CleanupRemoveClientEntryAfterThreshold || legacy.CleanupPurgePayloadAfterThreshold)
        {
            return DownloadHealthActionPresets.Replace;
        }
        if (legacy.CleanupBlockReleaseAfterThreshold || legacy.CleanupQueueReplacementAfterThreshold)
        {
            return DownloadHealthActionPresets.RetryElsewhere;
        }
        return DownloadHealthActionPresets.JustTellMe;
    }

    private static IReadOnlyList<DownloadProgressRecord> ReadDownloadProgressRecords(IReadOnlyDictionary<string, string> settings)
    {
        if (!settings.TryGetValue(DownloadProgressRecordsSettingKey, out var json) || string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<DownloadProgressRecord>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    /// <summary>Tells the Activity screen's download-health step a record was written or cleared. Sent after the commit; a missed push is caught up on reconnect.</summary>
    private Task AnnounceChangedAsync(string? clientId, CancellationToken cancellationToken)
        => realtimeEventPublisher.PublishSafelyAsync(publisher => publisher.PublishDownloadHealthChangedAsync(clientId ?? string.Empty, cancellationToken));

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

    private static IReadOnlyList<T> SafeDeserialize<T>(string json)
    {
        try { return JsonSerializer.Deserialize<List<T>>(json) ?? []; }
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
}
