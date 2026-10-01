using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Deluno.Infrastructure.Storage;

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
    DateTimeOffset ImportedUtc);

/// <summary>A release Deluno refused or a file it blocked, newest first on the Live screen's Protected-you counter.</summary>
public sealed record LiveProtection(string Release, string ReasonCode, string Reason, DateTimeOffset BlockedUtc);

public sealed record LiveProtected(int RefusedToday, int UnsafeToday, IReadOnlyList<LiveProtection> Latest);

/// <summary>A Seerr request Deluno turned into an add and search, and where it has got to.</summary>
public sealed record LiveRequest(string MediaType, string EntityId, string Title, string Status, string? RequestedBy, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc);

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
    long RetentionReclaimBytes);

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
        var imports = await ReadImportsAsync(sinceUtc, cancellationToken);
        var protectedYou = await ReadProtectedAsync(sinceUtc, cancellationToken);
        var requests = await ReadRequestsAsync(now.AddDays(-7), cancellationToken);
        var reclaim = await ReadReclaimBytesAsync(cancellationToken);
        return new DashboardLiveExtras(
            now,
            sinceUtc,
            imports.Take(40).ToArray(),
            imports.Count,
            imports.Sum(row => row.SizeBytes ?? 0),
            imports.Where(row => row.Upgraded).Take(12).ToArray(),
            protectedYou,
            requests,
            reclaim);
    }

    private async Task<List<LiveArrival>> ReadImportsAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken)
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
            LIMIT 500;
            """;
        Add(command, "@since", Iso(sinceUtc));
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            string? release = null, quality = null, previous = null;
            long? size = null;
            var upgraded = false;
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
                    upgraded = root.TryGetProperty("Upgraded", out var flag) && flag.ValueKind == JsonValueKind.True;
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
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)));
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

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

/// <summary>The Live extras, remembered for a few seconds per day so a burst of screens costs one read.</summary>
public sealed class DashboardLiveService(DashboardLiveStore store, TimeProvider timeProvider)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(20);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<DateTimeOffset, AsyncValueCache<DashboardLiveExtras>> _byDay = new();

    public Task<DashboardLiveExtras> GetAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        // Only the person's own midnight is ever asked for; a handful of days at most are held.
        if (_byDay.Count > 4)
        {
            _byDay.Clear();
        }

        var cache = _byDay.GetOrAdd(sinceUtc, _ => new AsyncValueCache<DashboardLiveExtras>(Lifetime, timeProvider));
        return cache.GetAsync(token => store.GetAsync(timeProvider.GetUtcNow(), sinceUtc, token), cancellationToken);
    }
}
