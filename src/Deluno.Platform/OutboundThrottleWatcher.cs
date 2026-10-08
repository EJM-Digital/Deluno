using Deluno.Contracts;
using Deluno.Infrastructure.Resilience;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Platform;

/// <summary>
/// Tells an open Indexers screen when Deluno starts or stops holding a host back (#1179), so the
/// "waiting on" figures change by themselves instead of the screen asking every five seconds.
/// Only while someone is on the dashboard subject does it look, every two seconds, and it pushes
/// <c>OutboundThrottleChanged</c> only when what the screen shows has changed.
/// </summary>
public sealed class OutboundThrottleWatcher(
    IOutboundRequestThrottle throttle,
    IRealtimeEventPublisher publisher,
    IRealtimeWatchers? watchers,
    TimeProvider timeProvider,
    ILogger<OutboundThrottleWatcher> logger)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private string? _last;

    public async Task TickAsync(CancellationToken cancellationToken)
    {
        if (watchers is null || watchers.Count("dashboard") == 0)
        {
            _last = null;
            return;
        }

        var signature = string.Join(
            "|",
            throttle.Describe()
                .OrderBy(state => state.Host, StringComparer.OrdinalIgnoreCase)
                .Select(state => $"{state.Host}:{state.Waiting}:{state.RefusedCount}:{state.GrantedCount}"));
        if (_last is not null && !string.Equals(_last, signature, StringComparison.Ordinal))
        {
            await publisher.PublishOutboundThrottleChangedAsync(cancellationToken);
        }

        _last = signature;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, timeProvider, stoppingToken);
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "An outbound throttle check was skipped.");
            }
        }
    }
}
