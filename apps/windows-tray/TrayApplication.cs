using System.Diagnostics;
using System.Reflection;

namespace Deluno.Tray;

public enum TrayState
{
    Starting,
    Running,
    Degraded,
    Error,
    Updating,
    Stopped
}

public sealed class TrayApplication : ApplicationContext
{
    private readonly NotifyIcon _notify;
    private readonly ToolStripMenuItem _openItem;
    private readonly ToolStripMenuItem _openUpdatesItem;
    private readonly ToolStripMenuItem _restartItem;
    private readonly ToolStripMenuItem _startStopItem;
    private readonly ToolStripMenuItem _serviceModeItem;
    private readonly DelunoServer _server;
    private TrayState _state = TrayState.Starting;

    public TrayApplication()
    {
        _server = new DelunoServer();

        _openItem = new ToolStripMenuItem("Open Deluno", null, OnOpen);
        _openUpdatesItem = new ToolStripMenuItem("Open Updates", null, OnOpenUpdates);
        _restartItem = new ToolStripMenuItem("Restart", null, OnRestart) { Enabled = false };
        _startStopItem = new ToolStripMenuItem("Stop", null, OnStartStop) { Enabled = false };
        _serviceModeItem = new ToolStripMenuItem("Run as Service...", null, OnServiceMode);

        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        var aboutItem = new ToolStripMenuItem($"Deluno v{version?.ToString(3) ?? "0.1.0"}") { Enabled = false };

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([
            _openItem,
            _openUpdatesItem,
            new ToolStripSeparator(),
            _restartItem,
            _startStopItem,
            new ToolStripSeparator(),
            _serviceModeItem,
            new ToolStripSeparator(),
            aboutItem,
            new ToolStripMenuItem("Quit", null, OnQuit)
        ]);

        _notify = new NotifyIcon
        {
            Text = "Deluno - Starting",
            Icon = TrayIconRenderer.Render(TrayState.Starting),
            ContextMenuStrip = menu,
            Visible = true
        };
        _notify.DoubleClick += OnOpen;

        SetState(TrayState.Starting);
        _ = StartServerAsync();
    }

    private void SetState(TrayState state)
    {
        _state = state;
        _notify.Icon = TrayIconRenderer.Render(state);
        _notify.Text = state switch
        {
            TrayState.Starting => "Deluno - Starting",
            TrayState.Running => $"Deluno - Running on port {AppSettings.Load().Port}",
            TrayState.Degraded => "Deluno - Running with warnings",
            TrayState.Error => "Deluno - Failed to start",
            TrayState.Updating => "Deluno - Updating",
            TrayState.Stopped => "Deluno - Stopped",
            _ => "Deluno"
        };

        _openItem.Enabled = state is TrayState.Running or TrayState.Degraded;
        _openUpdatesItem.Enabled = state is TrayState.Running or TrayState.Degraded;
        _restartItem.Enabled = state is TrayState.Running or TrayState.Degraded or TrayState.Error;
        _startStopItem.Enabled = state is not TrayState.Starting and not TrayState.Updating;
        _startStopItem.Text = state is TrayState.Stopped ? "Start" : "Stop";
    }

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

    private void OnOpen(object? sender, EventArgs e)
    {
        if (_state is TrayState.Running or TrayState.Degraded)
        {
            var port = AppSettings.Load().Port;
            Process.Start(new ProcessStartInfo($"http://localhost:{port}") { UseShellExecute = true });
        }
    }

    private void OnOpenUpdates(object? sender, EventArgs e)
    {
        if (_state is TrayState.Running or TrayState.Degraded)
        {
            var port = AppSettings.Load().Port;
            Process.Start(new ProcessStartInfo($"http://localhost:{port}/system/updates") { UseShellExecute = true });
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

    private async void OnServiceMode(object? sender, EventArgs e)
    {
        using var dialog = new ServiceModeDialog();
        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

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

    private void InvokeOnUiThread(Action action)
    {
        if (_notify.ContextMenuStrip?.InvokeRequired == true)
        {
            _notify.ContextMenuStrip.Invoke(action);
        }
        else
        {
            action();
        }
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
