using Deluno.Api.Network;
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
    private readonly ToolStripMenuItem _allowLanItem;
    private readonly ToolStripMenuItem _letAnyAddressInItem;
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
        _allowLanItem = new ToolStripMenuItem("Allow other devices on your network...", null, OnAllowLan) { Visible = false };
        _letAnyAddressInItem = new ToolStripMenuItem("Let any address in...", null, OnLetAnyAddressIn);

        var aboutItem = new ToolStripMenuItem($"Deluno v{Deluno.Api.DelunoVersion.Running}") { Enabled = false };

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([
            _openItem,
            _openUpdatesItem,
            _allowLanItem,
            new ToolStripSeparator(),
            _restartItem,
            _startStopItem,
            new ToolStripSeparator(),
            _serviceModeItem,
            _letAnyAddressInItem,
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
        _ = StartInstalledComponentsAsync();
    }

    /// <summary>
    /// After a restart, starts the download clients Deluno itself started at
    /// install if they are not answering (#1051, #1052). Never blocks or fails
    /// the tray; what it did is written to component-startup.log.
    /// </summary>
    private static async Task StartInstalledComponentsAsync()
    {
        try
        {
            var starter = new ComponentAutoStarter(
                new FileStartedComponentsRegistry(),
                new LoopbackPortProbe(),
                new RealProcessStarter(),
                message =>
                {
                    try
                    {
                        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Deluno", "logs");
                        Directory.CreateDirectory(logDir);
                        File.AppendAllText(Path.Combine(logDir, "component-startup.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
                    }
                    catch
                    {
                        // Best-effort.
                    }
                });
            await starter.RunAsync(BundledComponents.Load(), CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // A client that could not be started again is still startable by hand.
        }
    }

    private void SetState(TrayState state)
    {
        _state = state;
        _notify.Icon = TrayIconRenderer.Render(state);
        _notify.Text = state switch
        {
            TrayState.Starting => "Deluno - Starting",
            TrayState.Running => BuildRunningTooltip(),
            TrayState.Degraded => "Deluno - Running with warnings",
            TrayState.Error => "Deluno - Failed to start (see %LocalAppData%\\Deluno\\logs)",
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

    /// <summary>
    /// NotifyIcon tooltip text is capped at 63 chars by Win32. Show the
    /// most useful single line: prefer the LAN URL (what other devices use)
    /// and fall back to localhost. Full URL list lives in the startup log.
    /// </summary>
    private string BuildRunningTooltip()
    {
        var port = _server.ListeningPort ?? AppSettings.Load().Port;
        var urls = _server.ReachableUrls;
        var lan = urls.FirstOrDefault(u => !u.Contains("localhost", StringComparison.OrdinalIgnoreCase));
        var primary = lan ?? $"http://localhost:{port}/";
        var text = $"Deluno - {primary.TrimEnd('/')}";
        // Truncate hard so the tooltip doesn't get silently chopped by Win32.
        return text.Length > 63 ? text.Substring(0, 60) + "..." : text;
    }

    private async Task StartServerAsync()
    {
        try
        {
            SetState(TrayState.Starting);
            await _server.StartAsync();
            InvokeOnUiThread(() =>
            {
                SetState(TrayState.Running);
                _allowLanItem.Visible = _server.LanAccess?.CanAllow == true;
                ShowReachableUrlsBalloon();
            });
        }
        catch (Exception ex)
        {
            InvokeOnUiThread(() =>
            {
                SetState(TrayState.Error);
                // Persistent (~30s) so the user has time to read it. Also write the
                // full stack trace to the startup log for diagnosis later.
                _notify.ShowBalloonTip(30000, "Deluno failed to start", ex.Message + "  (see %LocalAppData%\\Deluno\\logs\\tray-startup.log)", ToolTipIcon.Error);
                TryWriteStartupLog(ex);
            });
        }
    }

    private void ShowReachableUrlsBalloon()
    {
        var urls = _server.ReachableUrls;
        if (urls.Count == 0) return;

        var lanUrls = urls.Where(u => !u.Contains("localhost", StringComparison.OrdinalIgnoreCase)).ToList();
        var title = "Deluno is running";
        var body = lanUrls.Count == 0
            ? $"Local only: {urls[0]}"
            : $"LAN: {lanUrls[0]}{(lanUrls.Count > 1 ? $" (+{lanUrls.Count - 1} more)" : string.Empty)}";

        // Other devices are turned away until the firewall lets them in (#623).
        if (_server.LanAccess?.State == LanAccessStates.Blocked)
        {
            body += "  - Windows Firewall blocks other devices. Right-click the Deluno icon and choose Allow other devices.";
        }
        _notify.ShowBalloonTip(10000, title, body, ToolTipIcon.Info);
    }

    /// <summary>
    /// Asks Windows, through its permission prompt, to let other devices reach
    /// Deluno, and says what happened (#623).
    /// </summary>
    private async void OnAllowLan(object? sender, EventArgs e)
    {
        if (_server.Firewall is null)
        {
            return;
        }

        _allowLanItem.Enabled = false;
        var status = await _server.Firewall.AllowAsync(CancellationToken.None);
        InvokeOnUiThread(() =>
        {
            _allowLanItem.Enabled = true;
            _allowLanItem.Visible = status.CanAllow;
            _notify.ShowBalloonTip(
                10000,
                status.State == LanAccessStates.Open ? "Other devices can reach Deluno" : "Other devices still cannot reach Deluno",
                status.Message,
                status.State == LanAccessStates.Open ? ToolTipIcon.Info : ToolTipIcon.Warning);
        });
    }

    /// <summary>
    /// The Windows tray recovery action for Allowed Hosts (#939): if every
    /// address Deluno can be reached on ends up refused, this needs no
    /// working web UI to fix -- it writes the same
    /// <c>Security:AllowedHosts:ForceDisabled</c> override
    /// docs/security-allowed-hosts.md documents for the config file, then
    /// restarts the embedded server so it takes effect immediately.
    /// </summary>
    private async void OnLetAnyAddressIn(object? sender, EventArgs e)
    {
        var confirmed = MessageBox.Show(
            "Deluno will answer requests on any address again, exactly as it did before Allowed Hosts existed. " +
            "You can turn Allowed Hosts back on from System → Security once you can reach Deluno again.",
            "Let any address in?",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning);
        if (confirmed != DialogResult.OK)
        {
            return;
        }

        var settings = AppSettings.Load();
        settings.AllowedHostsForceDisabled = true;
        settings.Save();

        _letAnyAddressInItem.Enabled = false;
        await _server.StopAsync();
        await StartServerAsync();
        InvokeOnUiThread(() =>
        {
            _letAnyAddressInItem.Enabled = true;
            _notify.ShowBalloonTip(
                10000,
                "Deluno answers on any address again",
                "Allowed Hosts is off until you turn it back on from System → Security.",
                ToolTipIcon.Info);
        });
    }

    private static void TryWriteStartupLog(Exception ex)
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Deluno", "logs");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "tray-startup.log");
            File.AppendAllText(logPath,
                $"{DateTimeOffset.Now:O} server start failure{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Best-effort.
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
