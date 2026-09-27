using Deluno.Connections.Data;
using Deluno.Contracts;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>
/// A media server whose refresh keeps failing (#920): one row per server and
/// media type still in the retry backlog, said once - "Plex hasn't answered
/// since 21:04" - never once per missed title, and gone the moment a retry
/// or an ordinary notify catches it up.
/// </summary>
public sealed class MediaServerRefreshHealthCheck(IMediaServerRefreshBacklogRepository backlog, IMediaServerRepository servers) : IHealthCheck
{
    public string Category => HealthCheckCategories.MediaServer;

    public async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        var pending = await backlog.ListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return [];
        }

        var known = (await servers.ListAsync(cancellationToken)).ToDictionary(server => server.Id, StringComparer.OrdinalIgnoreCase);
        var outcomes = new List<HealthCheckOutcome>(pending.Count);
        foreach (var item in pending)
        {
            var name = known.TryGetValue(item.MediaServerId, out var server) ? server.Name : "A removed media server";
            var since = item.FirstFailedUtc.ToLocalTime().ToString("HH:mm");
            var library = item.MediaType == "tv" ? "TV" : "movies";
            outcomes.Add(HealthCheckOutcome.Problem(
                $"media-server-refresh:{item.MediaServerId}:{item.MediaType}",
                Category,
                $"{name} ({library} refresh)",
                $"{name} hasn't answered since {since}.",
                cause: $"{item.Changes.Count} changed file{(item.Changes.Count == 1 ? string.Empty : "s")} could not be told to it yet.",
                fix: "Deluno keeps retrying with backoff and will catch it up in one go the moment it answers. Open Connections to check on it sooner.",
                detailsJson: $$"""{"mediaServerId":"{{item.MediaServerId}}","mediaType":"{{item.MediaType}}","pendingCount":{{item.Changes.Count}},"attemptCount":{{item.AttemptCount}}}"""));
        }

        return outcomes;
    }
}
