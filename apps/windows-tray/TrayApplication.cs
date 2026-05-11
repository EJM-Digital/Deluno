using System.Diagnostics;
using System.Reflection;

namespace Deluno.Tray;

public enum TrayState { Starting, Running, Degraded, Error, Updating, Stopped }

public sealed class TrayApplication : ApplicationContext
{
    private readonly NotifyIcon _notify;
    private readonly ToolStripMenuItem _openItem;
    private readonly ToolStripMenuItem _restartItem;
    private readonly ToolStripMenuItem _startStopItem;
    private readonly ToolStripMenuItem _updateItem;
    private readonly ToolStripMenuItem _serviceModeItem;
    private readonly DelunoServer _server;
    private readonly UpdateChecker _updater;
    private TrayState _state = TrayState.Starting;
    private string? _pendingUpdateVersion;

    public TrayApplication()
    {
        _server = new DelunoServer();
        _updater = new UpdateChecker();

        _openItem       = new ToolStripMenuItem("Open Deluno",          null, OnOpen);
        _restartItem    = new ToolStripMenuItem("Restart",              null, OnRestart) { Enabled = false };
        _startStopItem  = new ToolStripMenuItem("Stop",                 null, OnStartStop) { Enabled = false };
        _updateItem     = new ToolStripMenuItem("Check for Updates",    null, OnCheckUpdate);
        _serviceModeItem = new ToolStripMenuItem("Run as Service...",   null, OnServiceMode);

        var version = Assembly.GetEntryAssembly()!.GetName().Version;
        var aboutItem = new ToolStripMenuItem($"Deluno v{version?.ToString(3) ?? "0.1.0"}") { Enabled = false };

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([
            _openItem,
            new ToolStripSeparator(),
            _restartItem,
            _startStopItem,
            new ToolStripSeparator(),
            _serviceModeItem,
            new ToolStripSeparator(),
            _updateItem,
            new ToolStripSeparator(),
            aboutItem,
            new ToolStripMenuItem("Quit", null, OnQuit)
        ]);

        _notify = new NotifyIcon
        {
            Text = "Deluno — Starting…",
            Icon = TrayIconRenderer.Render(TrayState.Starting),
            ContextMenuStrip = menu,
            Visible = true
        };
        _notify.DoubleClick += OnOpen;

        SetState(TrayState.Starting);
        _ = StartServerAsync();
        _ = ScheduleUpdateChecksAsync();
    }

    // ── State management ───────────────────────────────────────────────────

    private void SetState(TrayState state)
    {
        _state = state;
        _notify.Icon = TrayIconRenderer.Render(state);
        _notify.Text = state switch
        {
            TrayState.Starting  => "Deluno — Starting…",
            TrayState.Running   => $"Deluno — Running on port {AppSettings.Load().Port}",
            TrayState.Degraded  => "Deluno — Running with warnings",
            TrayState.Error     => "Deluno — Failed to start",
            TrayState.Updating  => "Deluno — Updating…",
            TrayState.Stopped   => "Deluno — Stopped",
            _                   => "Deluno"
        };

        _openItem.Enabled     = state is TrayState.Running or TrayState.Degraded;
        _restartItem.Enabled  = state is TrayState.Running or TrayState.Degraded or TrayState.Error;
        _startStopItem.Enabled = state is not TrayState.Starting and not TrayState.Updating;
        _startStopItem.Text   = state is TrayState.Stopped ? "Start" : "Stop";

        if (_pendingUpdateVersion is not null)
        {
            _updateItem.Text = $"Update to {_pendingUpdateVersion}";
            _updateItem.Font = new Font(_updateItem.Font, FontStyle.Bold);
        }
    }

    // ── Server lifecycle ───────────────────────────────────────────────────

    private async Task StartServerAsync()
    {
        try
        {
            SetState(TrayState.Starting);
            await _server.StartAsync();
            InvokeOnUiThread(() => SetState(TrayState.Running));
        }
        catch (Exception ex)
        {
            InvokeOnUiThread(() =>
            {
                SetState(TrayState.Error);
                _notify.ShowBalloonTip(5000, "Deluno failed to start", ex.Message, ToolTipIcon.Error);
            });
        }
    }

    // ── Context menu handlers ──────────────────────────────────────────────

    private void OnOpen(object? sender, EventArgs e)
    {
        if (_state is TrayState.Running or TrayState.Degraded)
        {
            var port = AppSettings.Load().Port;
            Process.Start(new ProcessStartInfo($"http://localhost:{port}") { UseShellExecute = true });
        }
    }

    private async void OnRestart(object? sender, EventArgs e)
    {
        _restartItem.Enabled = false;
        await _server.StopAsync();
        await StartServerAsync();
    }

    private async void OnStartStop(object? sender, EventArgs e)
    {
        if (_state is TrayState.Stopped)
        {
            await StartServerAsync();
        }
        else
        {
            await _server.StopAsync();
            InvokeOnUiThread(() => SetState(TrayState.Stopped));
        }
    }

    private async void OnCheckUpdate(object? sender, EventArgs e)
    {
        _updateItem.Enabled = false;
        _updateItem.Text = "Checking…";

        var update = await _updater.CheckAsync();
        if (update is null)
        {
            _updateItem.Text = "Up to date";
            await Task.Delay(3000);
            _updateItem.Text = "Check for Updates";
            _updateItem.Enabled = true;
            return;
        }

        _pendingUpdateVersion = update.Version;
        InvokeOnUiThread(() =>
        {
            SetState(_state); // refreshes update item label
            _updateItem.Enabled = true;
            _notify.ShowBalloonTip(6000,
                $"Deluno {update.Version} available",
                "Click 'Update to…' in the tray menu to install.",
                ToolTipIcon.Info);
        });
    }

    private async void OnServiceMode(object? sender, EventArgs e)
    {
        using var dialog = new ServiceModeDialog();
        if (dialog.ShowDialog() != DialogResult.OK) return;

        SetState(TrayState.Updating);
        await _server.StopAsync();
        ServiceManager.Apply(dialog.SelectedMode, dialog.ServiceUsername, dialog.ServicePassword);
        Application.Exit();
    }

    private void OnQuit(object? sender, EventArgs e)
    {
        _server.StopAsync().GetAwaiter().GetResult();
        _notify.Visible = false;
        Application.Exit();
    }

    // ── Update scheduling ──────────────────────────────────────────────────

    private async Task ScheduleUpdateChecksAsync()
    {
        // First check shortly after startup, then every 24 hours
        await Task.Delay(TimeSpan.FromSeconds(30));
        while (true)
        {
            var update = await _updater.CheckAsync();
            if (update is not null)
            {
                _pendingUpdateVersion = update.Version;
                InvokeOnUiThread(() =>
                {
                    SetState(_state);
                    _notify.ShowBalloonTip(6000,
                        $"Deluno {update.Version} available",
                        "Open the tray menu to update.",
                        ToolTipIcon.Info);
                });
            }
            await Task.Delay(TimeSpan.FromHours(24));
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private void InvokeOnUiThread(Action action)
    {
        if (_notify.ContextMenuStrip?.InvokeRequired == true)
            _notify.ContextMenuStrip.Invoke(action);
        else
            action();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _notify.Visible = false;
            _notify.Dispose();
            _server.Dispose();
        }
        base.Dispose(disposing);
    }
}
