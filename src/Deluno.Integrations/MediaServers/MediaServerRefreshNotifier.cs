using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;

namespace Deluno.Integrations.MediaServers;

/// <summary>
/// Tells every configured, enabled media server about changed files (#662) -
/// the one place import, upgrade, season-pack, rename and manual removal all
/// end up. Always run from a background job
/// (<see cref="MediaServerJobTypes.Refresh"/>), never inline with the import
/// itself: a slow or down server must never hold up or fail the file that
/// already landed.
///
/// <para><b>A failure is retried, not dropped (#920).</b> When a server does
/// not answer, what it missed is kept per server and media type
/// (<see cref="IMediaServerRefreshBacklogRepository"/>) and a
/// <see cref="MediaServerJobTypes.RefreshRetry"/> job is scheduled with
/// backoff. Every later notify for that server folds its own changes into
/// the same backlog rather than racing it, so nothing is told twice and
/// nothing is lost. The moment it answers - on an ordinary notify or on its
/// own retry - everything missed since the first failure goes in one call,
/// and the backlog is cleared.</para>
/// </summary>
public interface IMediaServerRefreshNotifier
{
    Task NotifyAsync(string mediaType, IReadOnlyList<MediaServerPathChange> changes, CancellationToken cancellationToken);

    /// <summary>
    /// One server and media type's own retry (#920): sends everything still
    /// in its backlog. Returns whether the server answered - the retry job
    /// handler re-schedules itself with backoff when it did not.
    /// </summary>
    Task<bool> RetryAsync(string mediaServerId, string mediaType, CancellationToken cancellationToken);
}

public sealed class MediaServerRefreshNotifier(
    IMediaServerRepository repository,
    IMediaServerClientRegistry registry,
    TimeProvider timeProvider,
    IMediaServerRefreshBacklogRepository? backlog = null,
    IJobScheduler? jobScheduler = null,
    IActivityFeedRepository? activityFeed = null,
    IConnectionUseReporter? connectionUses = null) : IMediaServerRefreshNotifier
{
    public async Task NotifyAsync(string mediaType, IReadOnlyList<MediaServerPathChange> changes, CancellationToken cancellationToken)
    {
        if (changes.Count == 0)
        {
            return;
        }

        var servers = (await repository.ListAsync(cancellationToken)).Where(server => server.IsEnabled).ToArray();
        if (servers.Length == 0)
        {
            return;
        }

        var mappings = await repository.ListPathMappingsAsync(null, cancellationToken);

        foreach (var server in servers)
        {
            var client = registry.Find(server.Kind);
            if (client is null)
            {
                continue;
            }

            var serverMappings = mappings
                .Where(mapping => mapping.IsEnabled && string.Equals(mapping.MediaServerId, server.Id, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            // Anything this server still owes from an earlier failure goes
            // out with today's changes, in one call - never two, and never
            // silently dropped for what is new.
            var owed = backlog is null ? null : await backlog.FindAsync(server.Id, mediaType, cancellationToken);
            var toSend = Merge(owed?.Changes, changes);

            await AttemptAsync(server, mediaType, serverMappings, toSend, owed is not null, cancellationToken);
        }
    }

    public async Task<bool> RetryAsync(string mediaServerId, string mediaType, CancellationToken cancellationToken)
    {
        if (backlog is null)
        {
            return true;
        }

        var current = await backlog.FindAsync(mediaServerId, mediaType, cancellationToken);
        if (current is null || current.Changes.Count == 0)
        {
            // Already caught up by an ordinary notify in the meantime.
            return true;
        }

        var server = await repository.GetAsync(mediaServerId, cancellationToken);
        if (server is null || !server.IsEnabled)
        {
            await backlog.ClearAsync(mediaServerId, mediaType, cancellationToken);
            return true;
        }

        var client = registry.Find(server.Kind);
        if (client is null)
        {
            await backlog.ClearAsync(mediaServerId, mediaType, cancellationToken);
            return true;
        }

        var serverMappings = (await repository.ListPathMappingsAsync(server.Id, cancellationToken))
            .Where(mapping => mapping.IsEnabled)
            .ToArray();

        return await AttemptAsync(server, mediaType, serverMappings, current.Changes, wasFailing: true, cancellationToken);
    }

    /// <returns>Whether the server answered.</returns>
    private async Task<bool> AttemptAsync(
        MediaServerConnection server,
        string mediaType,
        IReadOnlyList<MediaServerPathMappingItem> serverMappings,
        IReadOnlyList<MediaServerPathChange> toSend,
        bool wasFailing,
        CancellationToken cancellationToken)
    {
        var translated = toSend.Select(change => change with { Path = TranslatePath(change.Path, serverMappings) }).ToArray();

        var started = timeProvider.GetUtcNow();
        var answer = await registry.Find(server.Kind)!.RefreshAsync(server, mediaType, translated, cancellationToken);
        var latencyMs = (int)Math.Round((timeProvider.GetUtcNow() - started).TotalMilliseconds);

        // A notify doubles as a health signal, the same way a Test click
        // does: failure raises a clearable finding on the server's own
        // row, success clears whatever a previous notify failure left
        // there (#662). A healthy server is left alone rather than
        // rewritten on every import.
        var (ok, status, message) = answer switch
        {
            MediaServerAnswer<MediaServerRefresh>.Reported reported => (true, "healthy", reported.Value.Message),
            MediaServerAnswer<MediaServerRefresh>.Unreachable unreachable => (false, "unreachable", unreachable.Reason),
            MediaServerAnswer<MediaServerRefresh>.NotSupported notSupported => (false, "degraded", notSupported.Reason),
            _ => (false, "degraded", $"{server.Name} could not be asked to refresh.")
        };

        var wasFlagged = server.HealthStatus is "unreachable" or "degraded";
        if (!ok || wasFlagged)
        {
            await repository.RecordHealthAsync(
                server.Id,
                new MediaServerTestResult(ok, status, message, latencyMs, server.ServerName, server.ServerId),
                cancellationToken);
        }

        if (ok)
        {
            // The server answered a refresh: its tile on Live lights and counts one more today (#1098).
            if (connectionUses is not null)
            {
                await connectionUses.ReportAsync(ConnectionKinds.MediaServer, server.Id, server.Name, cancellationToken);
            }

            // Told about new or changed files: one line of Activity naming the server and the files (#1100).
            if (activityFeed is not null && toSend.Count > 0)
            {
                var paths = toSend.Select(change => change.Path).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToArray();
                await activityFeed.RecordActivityAsync(
                    MediaServerRefreshActivity.Refreshed,
                    $"Told {server.Name} about {toSend.Count} new or changed file{(toSend.Count == 1 ? string.Empty : "s")}.",
                    JsonSerializer.Serialize(new { connectionName = server.Name, path = string.Join(';', paths) }),
                    null,
                    "media_server",
                    server.Id,
                    cancellationToken);
            }

            if (wasFailing && backlog is not null)
            {
                await backlog.ClearAsync(server.Id, mediaType, cancellationToken);
                if (activityFeed is not null)
                {
                    await activityFeed.RecordActivityAsync(
                        MediaServerRefreshActivity.CaughtUp,
                        $"{server.Name} answered again: told it about {toSend.Count} changed file{(toSend.Count == 1 ? string.Empty : "s")} it missed while it wasn't answering.",
                        null,
                        null,
                        "media_server",
                        server.Id,
                        cancellationToken);
                }
            }

            return true;
        }

        if (backlog is not null && jobScheduler is not null)
        {
            var recorded = await backlog.RecordFailureAsync(server.Id, mediaType, toSend, cancellationToken);
            await jobScheduler.EnqueueAsync(
                new EnqueueJobRequest(
                    MediaServerJobTypes.RefreshRetry,
                    "media-server-refresh-retry",
                    JsonSerializer.Serialize(new MediaServerRefreshRetryJobPayload(server.Id, mediaType)),
                    "media_server",
                    server.Id,
                    ScheduledUtc: recorded.NextAttemptUtc,
                    DedupeKey: RetryDedupeKey(server.Id, mediaType)),
                cancellationToken);
        }

        return false;
    }

    /// <summary>Backlog first, current second: a later change to the same path wins.</summary>
    private static IReadOnlyList<MediaServerPathChange> Merge(IReadOnlyList<MediaServerPathChange>? owed, IReadOnlyList<MediaServerPathChange> changes)
    {
        if (owed is null or { Count: 0 })
        {
            return changes;
        }

        var merged = new Dictionary<string, MediaServerPathChange>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in owed.Concat(changes))
        {
            merged[NormalizeForMatch(change.Path)] = change;
        }

        return merged.Values.ToArray();
    }

    internal static string RetryDedupeKey(string mediaServerId, string mediaType) => $"media-server-refresh-retry:{mediaServerId}:{mediaType}";

    /// <summary>
    /// Deluno's path translated to the server's own filesystem view, the
    /// longest matching root winning - the same rule a download-client path
    /// mapping uses, just facing the other direction. A path with no matching
    /// mapping is sent as-is, which is correct whenever Deluno and the server
    /// share the same filesystem.
    /// </summary>
    internal static string TranslatePath(string path, IReadOnlyList<MediaServerPathMappingItem> mappings)
    {
        var normalized = NormalizeForMatch(path);
        var match = mappings
            .Select(mapping => new { Mapping = mapping, Deluno = NormalizeForMatch(mapping.DelunoPath) })
            .Where(entry => normalized.Equals(entry.Deluno, StringComparison.OrdinalIgnoreCase) ||
                            normalized.StartsWith(entry.Deluno + "/", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.Deluno.Length)
            .ThenBy(entry => entry.Mapping.Priority)
            .FirstOrDefault();

        if (match is null)
        {
            return path;
        }

        var relative = normalized[match.Deluno.Length..].TrimStart('/');
        var serverRoot = match.Mapping.ServerPath.TrimEnd('/', '\\');
        if (relative.Length == 0)
        {
            return serverRoot;
        }

        // A Windows-shaped server root keeps Windows separators; anything else
        // (Docker, NAS, Linux) gets forward slashes.
        var separator = serverRoot.Contains('\\') && !serverRoot.Contains('/') ? '\\' : '/';
        return serverRoot + separator + relative.Replace('/', separator);
    }

    private static string NormalizeForMatch(string path) => path.Trim().TrimEnd('/', '\\').Replace('\\', '/');
}

/// <summary>Activity categories the refresh retry path writes under (#920).</summary>
public static class MediaServerRefreshActivity
{
    public const string CaughtUp = "media_server.refresh.caught_up";
    public const string Refreshed = "media_server.refreshed";
}
