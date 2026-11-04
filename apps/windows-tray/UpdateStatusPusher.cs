using Deluno.Api.Updates;
using Deluno.Contracts;

namespace Deluno.Tray;

/// <summary>
/// Tells the open Updates page, by the <c>UpdateStatusChanged</c> push, each time the Windows updater's state moves: a check
/// started, found, not found or failed; a download started, a step of it (at most about once a second); a download finished;
/// an apply (#1469). It listens to the state itself, so a check started from the tray pushes the same as one made through the
/// API. A state that did not change pushes nothing, which keeps a page that refreshes on each push from asking, pushing and
/// asking again.
/// </summary>
internal sealed class UpdateStatusPusher
{
    internal static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(1);

    private readonly UpdateStateMachine _state = new();
    private readonly IRealtimeEventPublisher? _realtime;
    private readonly TimeProvider _clock;
    private readonly object _lock = new();
    private string _lastPushed = string.Empty;
    private DateTimeOffset _lastProgressPush = DateTimeOffset.MinValue;

    /// <summary>The state the updater moves; the pusher listens to it.</summary>
    public UpdateStateMachine State => _state;

    public UpdateStatusPusher(IRealtimeEventPublisher? realtime, TimeProvider clock)
    {
        _realtime = realtime;
        _clock = clock;
        _state.Changed += OnChanged;
    }

    /// <summary>Records one download step, and pushes it unless one went out within the last second.</summary>
    public void ReportProgress(int percent)
    {
        _state.ReportProgress(percent);
        var now = _clock.GetUtcNow();
        lock (_lock)
        {
            if (now - _lastProgressPush < ProgressInterval)
            {
                return;
            }

            _lastProgressPush = now;
            _lastPushed = Signature();
        }

        Push();
    }

    private void OnChanged()
    {
        lock (_lock)
        {
            var signature = Signature();
            if (signature == _lastPushed)
            {
                return;
            }

            _lastPushed = signature;
            _lastProgressPush = _clock.GetUtcNow();
        }

        Push();
    }

    private string Signature()
        => $"{_state.State}|{_state.ProgressPercent}|{_state.LatestVersion}|{_state.RestartRequired}|{_state.LastError}|{_state.LastCheckedUtc:O}|{_state.LastDownloadedUtc:O}";

    private void Push()
    {
        if (_realtime is null)
        {
            return;
        }

        try
        {
            // Fire and forget: a push that fails must never fail a check or a download.
            _ = _realtime.PublishUpdateStatusChangedAsync(CancellationToken.None).ContinueWith(
                static t => _ = t.Exception,
                TaskContinuationOptions.OnlyOnFaulted);
        }
        catch
        {
            // The page catches up on its next read.
        }
    }
}
