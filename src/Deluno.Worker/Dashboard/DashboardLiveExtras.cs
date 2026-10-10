using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Data;

namespace Deluno.Worker.Dashboard;

/// <summary>One import that landed today: what it was, the quality it came in at and, for an upgrade, the quality it replaced.</summary>
public sealed record LiveArrival(
    string EntityType,
    string? EntityId,
    string? Release,
    string? Quality,
    string? PreviousQuality,
    bool Upgraded,
    long? SizeBytes,
    DateTimeOffset ImportedUtc,
    bool? QualityMet = null,
    string? LibraryId = null,
    string? LibraryName = null);

/// <summary>A release Deluno refused or a file it blocked, newest first on the Live screen's Protected-you counter.</summary>
public sealed record LiveProtection(string Release, string ReasonCode, string Reason, DateTimeOffset BlockedUtc);

public sealed record LiveProtected(int RefusedToday, int UnsafeToday, IReadOnlyList<LiveProtection> Latest);

/// <summary>A Seerr request Deluno turned into an add and search, and where it has got to.</summary>
public sealed record LiveRequest(string MediaType, string EntityId, string Title, string Status, string? RequestedBy, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc);

/// <summary>A release a search looked at and did not take today (#1098), newest first on the Live screen's Turned down list.</summary>
public sealed record LiveTurnedDownRelease(
    string Release,
    string Group,
    string Label,
    string? Title,
    string? EntityType,
    string? EntityId,
    DateTimeOffset AtUtc);

/// <summary>How many of today's turned-down releases fell in one plain group.</summary>
public sealed record LiveTurnedDownReason(string Group, string Label, int Count);

/// <summary>Releases Deluno looked at and did not take since the day began: the total, a bar per group (every group, zeros included) and the newest twenty.</summary>
public sealed record LiveTurnedDown(int Total, IReadOnlyList<LiveTurnedDownReason> Reasons, IReadOnlyList<LiveTurnedDownRelease> Latest);

/// <summary>One connection on the Live screen (#1098): a processor, media server, Seerr server, subtitle provider or list provider, and what it did today.</summary>
public sealed record LiveConnection(
    string Kind,
    string Id,
    string Name,
    bool Enabled,
    bool Healthy,
    int CountToday,
    int Busy);

/// <summary>What the Live screen shows beyond the Dashboard summary (#1096): today's arrivals and upgrades, what was refused, requests in flight, and what retention will give back.</summary>
public sealed record DashboardLiveExtras(
    DateTimeOffset AsOfUtc,
    DateTimeOffset SinceUtc,
    IReadOnlyList<LiveArrival> Arrivals,
    int ImportsToday,
    long ImportedBytesToday,
    IReadOnlyList<LiveArrival> Upgrades,
    LiveProtected Protected,
    IReadOnlyList<LiveRequest> Requests,
    long RetentionReclaimBytes,
    LiveTurnedDown? TurnedDown = null,
    int CleanedUpToday = 0,
    IReadOnlyList<LiveConnection>? Connections = null,
    IReadOnlyList<LiveArrival>? RecentArrivals = null);

/// <summary>
/// The stored rows behind <see cref="DashboardLiveExtras"/>. Each is one small
/// indexed query on a read-only connection; nothing reaches a client, an indexer
/// or a disk. The day is the person's own (the caller passes midnight).
/// </summary>
public sealed class DashboardLiveStore(IDelunoDatabaseConnectionFactory databases)
{
    /// <summary>Reasons a release was blocked because the file itself was wrong (a fake, a sample, not a video, broken), as opposed to a plain failure.</summary>
    private static readonly string[] UnsafeReasonCodes =
        ["noVideoStream", "likelySample", "unsupportedFile", "mediaProbeRejected", "mediaProbeUnreadable", "deadOrBrokenLink", "downloadIntegrityFailed"];

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    public async Task<DashboardLiveExtras> GetAsync(DateTimeOffset now, DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        var imports = await WithLibrariesAsync(await ReadImportsAsync(sinceUtc, 500, cancellationToken), cancellationToken);
        // The shelf shows the latest arrivals when nothing has come in today: the newest thirty of the last seven days.
        var recent = await WithLibrariesAsync(await ReadImportsAsync(now.AddDays(-7), 30, cancellationToken), cancellationToken);
        var protectedYou = await ReadProtectedAsync(sinceUtc, cancellationToken);
        var requests = await ReadRequestsAsync(now.AddDays(-7), cancellationToken);
        var reclaim = await ReadReclaimBytesAsync(cancellationToken);
        var turnedDown = await ReadTurnedDownAsync(sinceUtc, cancellationToken);
        var cleanedUp = await ReadCleanedUpAsync(sinceUtc, cancellationToken);
        var connections = await ReadConnectionsAsync(sinceUtc, cancellationToken);
        return new DashboardLiveExtras(
            now,
            sinceUtc,
            imports.Take(40).ToArray(),
            imports.Count,
            imports.Sum(row => row.SizeBytes ?? 0),
            imports.Where(row => row.Upgraded).Take(12).ToArray(),
            protectedYou,
            requests,
            reclaim,
            turnedDown,
            cleanedUp,
            connections,
            recent);
    }

    private async Task<List<LiveArrival>> ReadImportsAsync(DateTimeOffset sinceUtc, int limit, CancellationToken cancellationToken)
    {
        var rows = new List<LiveArrival>();
        await using var connection = await databases.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Jobs, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT related_entity_type, related_entity_id, details_json, created_utc
            FROM activity_events
            WHERE category = 'filesystem.import.completed' AND created_utc >= @since
            ORDER BY created_utc DESC
            LIMIT @limit;
            """;
        Add(command, "@since", Iso(sinceUtc));
        Add(command, "@limit", limit);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            string? release = null, quality = null, previous = null;
            long? size = null;
            var upgraded = false;
            bool? qualityMet = null;
            string? libraryId = null;
            var details = reader.IsDBNull(2) ? null : reader.GetString(2);
            if (!string.IsNullOrWhiteSpace(details))
            {
                try
                {
                    using var document = JsonDocument.Parse(details);
                    var root = document.RootElement;
                    release = Text(root, "ReleaseNameEvidence");
                    quality = Text(root, "NewQuality");
                    previous = Text(root, "PreviousQuality");
                    libraryId = Text(root, "LibraryId");
                    upgraded = root.TryGetProperty("Upgraded", out var flag) && flag.ValueKind == JsonValueKind.True;
                    if (root.TryGetProperty("QualityMet", out var met) && met.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        qualityMet = met.ValueKind == JsonValueKind.True;
                    }

                    if (root.TryGetProperty("FileSizeBytes", out var bytes) && bytes.ValueKind == JsonValueKind.Number)
                    {
                        size = bytes.GetInt64();
                    }
                }
                catch (JsonException)
                {
                    // Older events carry other details; they still count as an arrival.
                }
            }

            rows.Add(new LiveArrival(
                reader.IsDBNull(0) ? "movie" : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                release,
                quality,
                previous,
                upgraded,
                size,
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                qualityMet,
                libraryId));
        }

        return rows;
    }

    /// <summary>How many imports landed since <paramref name="sinceUtc"/> in each library (by library id), for the dashboard summary's per-library counts (#1103).</summary>
    public async Task<IReadOnlyDictionary<string, int>> CountArrivedByLibraryAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        var rows = await WithLibrariesAsync(await ReadImportsAsync(sinceUtc, 5000, cancellationToken), cancellationToken);
        return rows
            .Where(row => !string.IsNullOrWhiteSpace(row.LibraryId))
            .GroupBy(row => row.LibraryId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Says which library each import landed in (#1103). Newer imports record it; for older ones the title's own wanted-state
    /// row names the library it is held in. A library that has since been removed is left untagged.
    /// </summary>
    private async Task<List<LiveArrival>> WithLibrariesAsync(List<LiveArrival> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return rows;
        }

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using (var connection = await databases.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, name FROM libraries;";
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                names[reader.GetString(0)] = reader.GetString(1);
            }
        }

        var lookups = rows
            .Where(row => string.IsNullOrWhiteSpace(row.LibraryId) && !string.IsNullOrWhiteSpace(row.EntityId))
            .Select(row => (row.EntityType, EntityId: row.EntityId!))
            .Distinct()
            .ToArray();
        var found = new Dictionary<(string, string), string>();
        if (lookups.Length > 0)
        {
            await using var movies = await databases.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Movies, cancellationToken);
            await using var series = await databases.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Series, cancellationToken);
            foreach (var (type, id) in lookups)
            {
                var (connection, sql) = type.ToLowerInvariant() switch
                {
                    "series" => (series, "SELECT library_id FROM series_wanted_state WHERE series_id = @id ORDER BY has_file DESC, library_id LIMIT 1;"),
                    "episode" => (series, "SELECT library_id FROM episode_wanted_state WHERE episode_id = @id ORDER BY library_id LIMIT 1;"),
                    _ => (movies, "SELECT library_id FROM movie_wanted_state WHERE movie_id = @id ORDER BY has_file DESC, library_id LIMIT 1;")
                };
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                Add(command, "@id", id);
                if (await command.ExecuteScalarAsync(cancellationToken) is string library)
                {
                    found[(type, id)] = library;
                }
            }
        }

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var libraryId = string.IsNullOrWhiteSpace(row.LibraryId)
                ? (row.EntityId is not null && found.TryGetValue((row.EntityType, row.EntityId), out var held) ? held : null)
                : row.LibraryId;
            if (libraryId is not null && names.TryGetValue(libraryId, out var name))
            {
                rows[index] = row with { LibraryId = libraryId, LibraryName = name };
            }
            else if (libraryId is not null)
            {
                rows[index] = row with { LibraryId = null };
            }
        }

        return rows;
    }

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private async Task<LiveProtected> ReadProtectedAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        var latest = new List<LiveProtection>();
        int refused = 0, unsafeCount = 0;
        await using var connection = await databases.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Jobs, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT release_name, reason_code, reason, blocked_utc
            FROM blocked_releases
            WHERE blocked_utc >= @since AND state = 'refused'
            ORDER BY blocked_utc DESC
            LIMIT 200;
            """;
        Add(command, "@since", Iso(sinceUtc));
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            refused += 1;
            var code = reader.GetString(1);
            if (UnsafeReasonCodes.Contains(code, StringComparer.OrdinalIgnoreCase))
            {
                unsafeCount += 1;
            }

            if (latest.Count < 3)
            {
                latest.Add(new LiveProtection(
                    reader.GetString(0),
                    code,
                    reader.GetString(2),
                    DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)));
            }
        }

        return new LiveProtected(refused, unsafeCount, latest);
    }

    private async Task<IReadOnlyList<LiveRequest>> ReadRequestsAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        var rows = new List<LiveRequest>();
        await using var connection = await databases.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT media_type, entity_id, title, status, requested_by, created_utc, updated_utc
            FROM seerr_acquisitions
            WHERE created_utc >= @since
            ORDER BY created_utc DESC
            LIMIT 12;
            """;
        Add(command, "@since", Iso(sinceUtc));
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new LiveRequest(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)));
        }

        return rows;
    }

    /// <summary>What retention will give back: the size of every title it has marked as leaving or ready to go.</summary>
    private async Task<long> ReadReclaimBytesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databases.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(size_bytes), 0) FROM retention_marks WHERE state IN ('leaving', 'ready');";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    /// <summary>Each distinct release (by name) counts once a day, as its newest row today: a search that meets it again only adds a row, never a release.</summary>
    private const string LatestRowToday =
        "t.searched_utc >= @since AND t.rowid = (SELECT u.rowid FROM turned_down_releases u WHERE u.release_name = t.release_name COLLATE NOCASE AND u.searched_utc >= @since ORDER BY u.searched_utc DESC, u.rowid DESC LIMIT 1)";

    /// <summary>Releases looked at and not taken since the day began, from the record each search leaves (#1098): one grouped count and the newest twenty.</summary>
    private async Task<LiveTurnedDown> ReadTurnedDownAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var latest = new List<LiveTurnedDownRelease>();
        await using var connection = await databases.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Jobs, cancellationToken);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT reason_group, raw_reason, COUNT(*) FROM turned_down_releases t WHERE {LatestRowToday} GROUP BY reason_group, raw_reason;";
            Add(command, "@since", Iso(sinceUtc));
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                // A row stored as "other" is read again from its raw reason, so rows from before a group existed still land in it.
                var group = Regrouped(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
                counts[group] = counts.GetValueOrDefault(group) + reader.GetInt32(2);
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                $"""
                SELECT release_name, reason_group, title, entity_type, entity_id, searched_utc, raw_reason
                FROM turned_down_releases t
                WHERE {LatestRowToday}
                ORDER BY searched_utc DESC, rowid DESC
                LIMIT 20;
                """;
            Add(command, "@since", Iso(sinceUtc));
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var group = Regrouped(reader.GetString(1), reader.IsDBNull(6) ? null : reader.GetString(6));
                latest.Add(new LiveTurnedDownRelease(
                    reader.GetString(0),
                    group,
                    TurnedDownGroups.Label(group),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)));
            }
        }

        var reasons = TurnedDownGroups.All
            .Select(group => new LiveTurnedDownReason(group, TurnedDownGroups.Label(group), counts.GetValueOrDefault(group)))
            .ToArray();
        return new LiveTurnedDown(reasons.Sum(reason => reason.Count), reasons, latest);
    }

    private static string Regrouped(string stored, string? raw)
        => string.Equals(stored, TurnedDownGroups.Other, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(raw)
            ? TurnedDownReasons.GroupFor(raw)
            : stored;

    /// <summary>Finished downloads removed from a client today after they had shared enough (retention); the library copy stays.</summary>
    private async Task<int> ReadCleanedUpAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        await using var connection = await databases.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Jobs, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM activity_events WHERE category = '" + SeedingCleanupActivity.Category + "' AND created_utc >= @since;";
        Add(command, "@since", Iso(sinceUtc));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Everything else Deluno is connected to, one entry each (#1098): processors, media servers, Seerr, subtitle providers
    /// and each family of import lists, with what it did today. The names are the ones the person gave.
    /// </summary>
    private async Task<IReadOnlyList<LiveConnection>> ReadConnectionsAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        var days = await new ConnectionDayStore(databases).ReadAsync(sinceUtc, cancellationToken);
        ConnectionDay Day(string kind, string id) => days.TryGetValue(ConnectionDayStore.Key(kind, id), out var day) ? day : new ConnectionDay(0, 0);

        var connections = new List<LiveConnection>();
        await using var connection = await databases.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);

        async Task ReadAsync(string kind, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetString(0);
                var day = Day(kind, id);
                connections.Add(new LiveConnection(
                    kind,
                    id,
                    reader.GetString(1),
                    reader.GetInt32(2) != 0,
                    !ConnectionHealthRules.IsDown(reader.IsDBNull(3) ? null : reader.GetString(3)),
                    day.Count,
                    day.Busy));
            }
        }

        await ReadAsync(ConnectionKinds.Processor, "SELECT id, name, is_enabled, health_status FROM processor_connections ORDER BY name COLLATE NOCASE;");
        await ReadAsync(ConnectionKinds.MediaServer, "SELECT id, name, is_enabled, health_status FROM media_servers ORDER BY name COLLATE NOCASE;");
        await ReadAsync(ConnectionKinds.Requests, "SELECT id, name, is_enabled, health_status FROM seerr_servers ORDER BY name COLLATE NOCASE;");
        await ReadAsync(ConnectionKinds.Subtitles, "SELECT id, name, is_enabled, health_status FROM subtitle_providers ORDER BY name COLLATE NOCASE;");

        // Lists are one tile per provider family ("Trakt", "TMDb"...) with how many lists are being watched.
        var families = new Dictionary<string, (string Name, bool Enabled, bool Healthy)>(StringComparer.OrdinalIgnoreCase);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT provider, is_enabled, last_sync_status FROM intake_sources;";
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var provider = reader.GetString(0);
                var key = ListProviderFamilies.KeyFor(provider);
                var enabled = reader.GetInt32(1) != 0;
                var status = reader.IsDBNull(2) ? null : reader.GetString(2);
                var failed = enabled && status is "error" or "failed";
                var known = families.TryGetValue(key, out var existing) ? existing : (Name: ListProviderFamilies.NameFor(provider), Enabled: false, Healthy: true);
                families[key] = (known.Name, known.Enabled || enabled, known.Healthy && !failed);
            }
        }

        foreach (var (key, family) in families.OrderBy(pair => pair.Value.Name, StringComparer.OrdinalIgnoreCase))
        {
            connections.Add(new LiveConnection(ConnectionKinds.Lists, key, family.Name, family.Enabled, family.Healthy, Day(ConnectionKinds.Lists, key).Count, 0));
        }

        return connections;
    }

    /// <summary>Last contact worked, or it has never failed: anything that is not a recorded failure.</summary>
    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

/// <summary>The Live extras, remembered for five seconds per day so a burst of screens costs one read.</summary>
public sealed class DashboardLiveService(DashboardLiveStore store, TimeProvider timeProvider)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<DateTimeOffset, AsyncValueCache<DashboardLiveExtras>> _byDay = new();

    /// <param name="fresh">True when a health change was just pushed (#1233): the remembered copy is dropped so the connections are read as they are now.</param>
    public Task<DashboardLiveExtras> GetAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken, bool fresh = false)
    {
        // Only the person's own midnight is ever asked for; a handful of days at most are held.
        if (_byDay.Count > 4)
        {
            _byDay.Clear();
        }

        var cache = _byDay.GetOrAdd(sinceUtc, _ => new AsyncValueCache<DashboardLiveExtras>(Lifetime, timeProvider));
        if (fresh)
        {
            cache.Invalidate();
        }

        return cache.GetAsync(token => store.GetAsync(timeProvider.GetUtcNow(), sinceUtc, token), cancellationToken);
    }
}
