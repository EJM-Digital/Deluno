using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Worker.Services;

/// <summary>
/// Publishes what transfers are actually doing, as they do it (#273).
///
/// There was exactly one publisher of <c>DownloadProgress</c> in the codebase
/// and it fired once, when a grab was dispatched, with progress and speed both
/// zero. So the pipeline card had CountUp on every stage, pulsing lights, motes
/// travelling between nodes and a half-second transition on every progress bar
/// — over data that was frozen for up to a minute. An animated shell above
/// stale numbers reads as working, which is worse than reading as idle.
///
/// It also carried the *dispatch* id while the frontend keys its rows by the
/// download client's queue-item id, so anything joining on it would silently
/// have applied to nothing.
///
/// This publishes the client's own readings under the client's own id, which is
/// the id the rows already use.
/// </summary>
public sealed class DownloadProgressPublisher(
    ILogger<DownloadProgressPublisher> logger,
    IServiceScopeFactory scopeFactory,
    IRealtimeEventPublisher realtimeEventPublisher,
    TimeProvider timeProvider,
    IRealtimeWatchers? watchers = null)
    : BackgroundService
{
    /// <summary>
    /// How often to look while something is moving. Fast enough that a bar
    /// climbs rather than jumps, slow enough that it is one download-client
    /// call every couple of seconds rather than a stream of them.
    /// </summary>
    private static readonly TimeSpan ActiveInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How often to look when nothing is. An idle install should not be asking
    /// its download client anything on a two-second clock, and the queue's own
    /// events say when something arrives.
    /// </summary>
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How often to look while something is moving and a screen (Live or
    /// Transfers) is open (#1097): once a second, with a speed sample each time.
    /// Nobody watching means the ordinary interval, so there is no extra cost.
    /// </summary>
    internal static readonly TimeSpan WatchedActiveInterval = TimeSpan.FromSeconds(1);

    internal static TimeSpan NextInterval(bool moving, bool watched)
        => !moving ? IdleInterval : watched ? WatchedActiveInterval : ActiveInterval;

    private bool IsWatched => watchers is not null && watchers.Count(LiveRealtimeSubject.Name) > 0;

    /// <summary>
    /// Below this, a reading is the same reading. Without it a forty-file
    /// season would emit an event per file per tick for changes no one can see
    /// — the volume constraint in #273, met by saying less rather than by
    /// throttling afterwards.
    /// </summary>
    private const double ProgressEpsilon = 0.5;
    private const double SpeedEpsilonMbps = 0.1;

    /// <summary>
    /// What was last said about each item, so nothing is said twice. Bounded by
    /// the client queue, and pruned to it every pass, so a long-running install
    /// cannot accumulate entries for items that are long gone.
    /// </summary>
    private readonly Dictionary<string, Published> _published = new(StringComparer.OrdinalIgnoreCase);

    private bool _reportedFailure;

    private readonly Dictionary<string, (bool Reachable, bool? Paused)> _clientStates = new(StringComparer.OrdinalIgnoreCase);

    private readonly record struct Published(double Progress, double SpeedMbps, string Status);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = IdleInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                interval = NextInterval(await PublishAsync(stoppingToken), IsWatched);
                _reportedFailure = false;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                interval = IdleInterval;

                // An unreachable client is exactly the situation someone is
                // watching this card to understand, so it must not take the
                // worker down. Said out loud once; a permanently broken
                // publisher should not look identical to a blip.
                if (_reportedFailure)
                {
                    logger.LogDebug(exception, "Download progress publish skipped.");
                }
                else
                {
                    _reportedFailure = true;
                    logger.LogWarning(exception, "Download progress publishing failed; transfers will move on the poll instead.");
                }
            }
        }
    }

    /// <summary>Returns true when something is still moving and the fast cadence is earned.</summary>
    private async Task<bool> PublishAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var telemetry = scope.ServiceProvider.GetRequiredService<IDownloadClientTelemetryService>();
        var overview = await telemetry.GetFreshOverviewAsync(DownloadTelemetryFreshness.Loop, cancellationToken);

        var live = overview.Clients
            .SelectMany(client => client.Queue)
            // NotTransferring (#660): a checking or moving torrent is not
            // stalled, but it is still doing something a person watching this
            // card should see change, so it earns the same live cadence as an
            // active transfer rather than looking frozen until it flips to a
            // status this filter already knew.
            .Where(item => item.Status is DownloadQueueStatuses.Downloading or DownloadQueueStatuses.Stalled or DownloadQueueStatuses.Queued or DownloadQueueStatuses.NotTransferring)
            .ToArray();

        await PublishClientStatesAsync(overview, cancellationToken);

        // A speed sample with every reading while someone watches (#1097), so a
        // chart moves once a second whether or not any one item changed.
        if (IsWatched)
        {
            await realtimeEventPublisher.PublishDownloadSpeedSampleAsync(
                new DownloadSpeedSampleEvent(
                    Math.Round(overview.Summary.TotalSpeedMbps, 2),
                    Math.Round(overview.Summary.TotalUploadSpeedMbps, 2),
                    live.Count(item => item.Status == DownloadQueueStatuses.Downloading),
                    timeProvider.GetUtcNow().ToString("O")),
                cancellationToken);
        }

        foreach (var item in live)
        {
            var current = new Published(item.Progress, item.SpeedMbps, item.Status);
            if (_published.TryGetValue(item.Id, out var last) && IsSameReading(last, current))
            {
                continue;
            }

            _published[item.Id] = current;

            await realtimeEventPublisher.PublishDownloadProgressAsync(
                item.Id,
                item.Title,
                item.Progress,
                item.SpeedMbps,
                item.EtaSeconds > 0 ? TimeSpan.FromSeconds(item.EtaSeconds).ToString(@"hh\:mm\:ss") : null,
                item.Status,
                cancellationToken);
        }

        // Anything the client has stopped reporting has finished, been removed,
        // or moved past downloading. Its last reading is no longer worth
        // remembering, and keeping it would leak a row per transfer forever.
        var stillPresent = live.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _published.Keys.Where(id => !stillPresent.Contains(id)).ToArray())
        {
            _published.Remove(gone);
        }

        return live.Length > 0;
    }

    /// <summary>Says so the moment a client turns unreachable, comes back, is paused or resumed (#1097).</summary>
    private async Task PublishClientStatesAsync(DownloadTelemetryOverview overview, CancellationToken cancellationToken)
    {
        foreach (var client in overview.Clients)
        {
            var state = (Reachable: client.LastFailure is null, Paused: client.ClientPaused);
            var known = _clientStates.TryGetValue(client.ClientId, out var last);
            _clientStates[client.ClientId] = state;
            if (!known || last == state)
            {
                continue;
            }

            await realtimeEventPublisher.PublishClientStateChangedAsync(
                new ClientStateChangedEvent(
                    client.ClientId,
                    client.ClientName,
                    state.Reachable,
                    state.Paused,
                    client.LastFailure?.Message ?? client.LastHealthMessage,
                    timeProvider.GetUtcNow().ToString("O")),
                cancellationToken);
        }
    }

    private static bool IsSameReading(Published last, Published current)
        => string.Equals(last.Status, current.Status, StringComparison.OrdinalIgnoreCase)
           && Math.Abs(last.Progress - current.Progress) < ProgressEpsilon
           && Math.Abs(last.SpeedMbps - current.SpeedMbps) < SpeedEpsilonMbps;
}
