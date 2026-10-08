using Deluno.Contracts;
using Deluno.Platform.Data;
using Deluno.Realtime;
using Deluno.Worker.Dashboard;

namespace Deluno.Tray;

/// <summary>What the tray's red dot and paused mark are drawn from.</summary>
public sealed record TrayServerSignals(TrayAttention Attention, bool AutomationPaused);

/// <summary>
/// Follows the in-process server's own pushes (the same AttentionChanged, HealthChanged and Settings changes the open
/// bell listens to) and, when one arrives, reads the counts again. There is no timer: a burst of pushes is gathered
/// into one read after <see cref="Coalesce"/>, which is a pause for gathering, not a poll.
/// </summary>
public sealed class TrayAttentionWatcher : IDisposable
{
    /// <summary>The pushes that can move the bell's count, Health's problems or the Automation switch.</summary>
    public static bool Moves(string eventName) => eventName is
        LiveEventNames.AttentionChanged
        or "HealthChanged"
        or "SettingsChanged"
        or "AutomationStateChanged"
        or "DownloadClientChanged"
        or "IndexerChanged";

    public static readonly TimeSpan Coalesce = TimeSpan.FromMilliseconds(500);

    private readonly IServiceProvider _services;
    private readonly SignalRRealtimeEventPublisher? _publisher;
    private readonly Action<TrayServerSignals> _changed;
    private readonly object _lock = new();
    private bool _scheduled;
    private bool _disposed;

    public TrayAttentionWatcher(IServiceProvider services, Action<TrayServerSignals> changed)
    {
        _services = services;
        _changed = changed;
        _publisher = services.GetService(typeof(SignalRRealtimeEventPublisher)) as SignalRRealtimeEventPublisher;
        if (_publisher is not null)
        {
            _publisher.EventPublished += OnPublished;
        }
    }

    private void OnPublished(string eventName)
    {
        if (Moves(eventName))
        {
            Schedule();
        }
    }

    /// <summary>Reads the counts now (once the host is up), then follows the pushes.</summary>
    public void Schedule()
    {
        lock (_lock)
        {
            if (_scheduled || _disposed)
            {
                return;
            }

            _scheduled = true;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(Coalesce).ConfigureAwait(false);
            lock (_lock)
            {
                _scheduled = false;
                if (_disposed)
                {
                    return;
                }
            }

            try
            {
                _changed(await ReadAsync().ConfigureAwait(false));
            }
            catch
            {
                // A reading that failed leaves the mark as it was; the next push reads again.
            }
        });
    }

    /// <summary>The bell's count from the server's own counts, Health's problems, and the Automation switch.</summary>
    public async Task<TrayServerSignals> ReadAsync()
    {
        var summaries = _services.GetRequiredService<DashboardSummaryService>();
        summaries.Invalidate();
        var summary = await summaries.GetAsync(CancellationToken.None).ConfigureAwait(false);
        var health = await _services.GetRequiredService<IHealthCheckRepository>().ListAsync(CancellationToken.None).ConfigureAwait(false);
        var settings = await _services.GetRequiredService<IPlatformSettingsRepository>().GetAsync(CancellationToken.None).ConfigureAwait(false);
        return new TrayServerSignals(TrayAttention.From(summary, health, DateTimeOffset.UtcNow), !settings.AutoStartJobs);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
        }

        if (_publisher is not null)
        {
            _publisher.EventPublished -= OnPublished;
        }
    }
}
