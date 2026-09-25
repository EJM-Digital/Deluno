using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;

namespace Deluno.Integrations.MediaServers;

/// <summary>
/// Tells every configured, enabled media server about changed files (#662) -
/// the one place import, upgrade, season-pack, rename and manual removal all
/// end up. Always run from a background job
/// (<see cref="MediaServerJobTypes.Refresh"/>), never inline with the import
/// itself: a slow or down server must never hold up or fail the file that
/// already landed.
/// </summary>
public interface IMediaServerRefreshNotifier
{
    Task NotifyAsync(string mediaType, IReadOnlyList<MediaServerPathChange> changes, CancellationToken cancellationToken);
}

public sealed class MediaServerRefreshNotifier(
    IMediaServerRepository repository,
    IMediaServerClientRegistry registry,
    TimeProvider timeProvider) : IMediaServerRefreshNotifier
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
            var translated = changes.Select(change => change with { Path = TranslatePath(change.Path, serverMappings) }).ToArray();

            var started = timeProvider.GetUtcNow();
            var answer = await client.RefreshAsync(server, mediaType, translated, cancellationToken);
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
        }
    }

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
