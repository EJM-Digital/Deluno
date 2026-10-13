using Deluno.Api.Network;
using Deluno.Infrastructure.Storage;
using Deluno.Api.Updates;
using Deluno.Platform.Data;
using Deluno.Realtime;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

namespace Deluno.Tray;

/// <summary>
/// The notification-area icon, its mark, its hover text and its menu, as docs/tray-standard.md sets them. What each of
/// those says is decided in <see cref="TrayModel"/> (TrayBadges, TrayHoverText, TrayMenuState); this class draws it and
/// acts on the clicks.
/// </summary>
public sealed class TrayApplication : ApplicationContext
{
    private readonly NotifyIcon _notify;
    private readonly TrayIconTip _tip;
    private readonly ContextMenuStrip _menu;
    private readonly Control _ui = new();
    private readonly List<(TrayMenuId Id, ToolStripItem Item)> _items = [];
    private readonly List<(TrayMenuId Id, ToolStripItem Item)> _advancedItems = [];
    private readonly System.Windows.Forms.Timer _pendingInstallsTimer = new() { Interval = 5000 };
    private readonly OpenClickGate _openGate = new(TimeSpan.FromMilliseconds(SystemInformation.DoubleClickTime));
    private readonly ISignInEntry _signIn = new RunKeySignInEntry();
    private readonly DelunoServer _server;
    private readonly HostSupervisor _supervisor;
    private readonly StartProgress _progress;
    private readonly ProcessRelaunch _relaunch;
    private string? _lastStartNote;
    private bool _exitingForRestart;
    private readonly IDisposable? _secondLaunch;
    private TrayStatus _status;
    private OwnedIcon? _icon;
    private TrayIconSpec? _spec;
    private bool _dotShown = true;
    // Blinks the dot, and only while starting: it stops as soon as the first health reading arrives.
    private readonly System.Windows.Forms.Timer _blinkTimer = new() { Interval = 500 };
    private string _hover = string.Empty;
    private Action? _balloonClick;
    private bool _installPickerOpen;
    private bool _quitting;
    private DateTimeOffset? _offeredFor;
    private TrayHealthWatcher? _watcher;
    private VelopackUpdateOrchestrator? _updates;
    private string? _announcedUpdate;

    public TrayApplication(StartProgress progress)
    {
        _progress = progress;
        _server = new DelunoServer(progress);
        _ui.CreateControl();
        _ = _ui.Handle;

        _status = new TrayStatus
        {
            Version = Deluno.Api.DelunoVersion.Running,
            Port = DelunoServer.PortOverride ?? AppSettings.Load().Port,
            StartsWithWindows = SafeSignInEnabled()
        };

        _menu = new ContextMenuStrip { ShowItemToolTips = true };
        BuildMenu(TrayMenuState.Build(_status));

        _notify = new NotifyIcon { ContextMenuStrip = _menu, Visible = false };
        _tip = new TrayIconTip(new NotifyIconShell(_notify));
        // Shown at once, in the Starting state: never only after the server is up.
        ApplyStatus();
        _notify.Visible = true;

        _notify.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                OpenFromIcon();
            }
        };
        _notify.DoubleClick += (_, _) => OpenFromIcon();
        _notify.BalloonTipClicked += (_, _) =>
        {
            var action = _balloonClick;
            _balloonClick = null;
            action?.Invoke();
        };
        _menu.Opening += (_, _) => RefreshLocalStatus();
        _pendingInstallsTimer.Tick += (_, _) => CheckPendingInstalls();
        _pendingInstallsTimer.Start();
        _blinkTimer.Tick += (_, _) => OnBlink();

        _secondLaunch = SecondLaunchSignal.Listen(() => InvokeOnUiThread(ShowAlreadyRunning));

        VelopackUpdateOrchestrator.CleanStopBeforeApply = StopHostForUpdateAsync;

        var log = new TrayStartLog();
        _relaunch = new ProcessRelaunch(
            BlockedUpdateNote.DefaultFolder(),
            TimeProvider.System,
            new RealProcessLauncher(),
            Environment.ProcessId,
            Environment.ProcessPath,
            log);
        _supervisor = new HostSupervisor(
            new ServerHost(_server),
            _progress,
            new HostRestartReporter
            {
                Attempting = info => InvokeOnUiThread(() =>
                {
                    var retrying = info.Attempt > 1 || info.Reason == StartReason.UnexpectedStop;
                    Change(s => s with
                    {
                        State = TrayState.Starting,
                        RestartNote = retrying ? $"starting again (try {info.Attempt} of {info.Limit})" : null
                    });
                    if (retrying)
                    {
                        Balloon(
                            info.Reason == StartReason.UnexpectedStop ? "Deluno stopped by itself" : "Deluno did not start",
                            HostRestartReporter.RetryingMessage(info),
                            ToolTipIcon.Warning,
                            problem: false);
                    }
                }),
                Recovered = (reason, attempts) =>
                {
                    if (reason == StartReason.UnexpectedStop)
                    {
                        InvokeOnUiThread(() => Balloon("Deluno is running again", "Deluno stopped by itself and has been started again.", ToolTipIcon.Info, OpenDeluno));
                    }
                },
                GaveUp = (failures, window, last) => InvokeOnUiThread(() =>
                {
                    Change(s => s with { State = TrayState.Error, RestartNote = null, StartNote = null });
                    // The click is Restart: the one thing the person can do about it.
                    Balloon("Deluno could not start", HostRestartReporter.GaveUpMessage(failures, window, last), ToolTipIcon.Error, () => _ = RestartHostAsync());
                }),
                ProcessRestarting = reason => InvokeOnUiThread(() =>
                    Balloon("Deluno is restarting itself", "Deluno could not be stopped cleanly, so it is starting over. This takes a moment.", ToolTipIcon.Warning, problem: false))
            },
            log,
            TimeProvider.System,
            restarter: _relaunch,
            diagnostics: StartDiagnostics.Capture);
        _server.StoppedUnexpectedly += () => _ = HandleUnexpectedStopAsync();
        _progress.Changed += OnStartProgress;

        // A downloaded update is applied at the next start, before the host runs, so it starts clean.
        _ = StartServerAsync(StartReason.FirstStart);
        _ = StartInstalledComponentsAsync();
    }

    // ------------------------------------------------------------------ status, icon, hover text, menu

    private static bool SafeSignInEnabled()
    {
        try { return new RunKeySignInEntry().IsEnabled(); } catch { return false; }
    }

    /// <summary>Changes the status and redraws whatever it moves: the mark, the hover text and the menu.</summary>
    private void Change(Func<TrayStatus, TrayStatus> change)
    {
        _status = change(_status);
        ApplyStatus();
    }

    private void ApplyStatus()
    {
        var spec = TrayBadges.Choose(_status);
        if (!spec.Blinking)
        {
            _blinkTimer.Stop();
            _dotShown = true;
        }
        else if (!_blinkTimer.Enabled)
        {
            _blinkTimer.Start();
        }

        if (_spec != spec || _icon is null)
        {
            DrawIcon(spec);
            _spec = spec;
        }


        var hover = TrayHoverText.Build(_status);
        if (!string.Equals(hover, _hover, StringComparison.Ordinal))
        {
            _hover = hover;
            _tip.Publish(hover);
        }

        ApplyMenu(TrayMenuState.Build(_status));
    }

    /// <summary>Draws the icon and hands it to the shell, then frees the one it replaced.</summary>
    private void DrawIcon(TrayIconSpec spec)
    {
        var previous = _icon;
        var next = TrayIconRenderer.Render(spec, _dotShown);
        _notify.Icon = next.Icon;
        _icon = next;
        previous?.Dispose();
    }

    private void OnBlink()
    {
        if (_spec is not { Blinking: true })
        {
            return;
        }

        _dotShown = !_dotShown;
        DrawIcon(_spec);
    }

    private void BuildMenu(TrayMenuState state)
    {
        var handlers = Handlers();
        foreach (var entry in state.Entries)
        {
            ToolStripItem item = entry.Id == TrayMenuId.Separator ? new ToolStripSeparator() : MakeItem(entry, handlers);
            if (entry.Id == TrayMenuId.Advanced && item is ToolStripMenuItem advanced)
            {
                foreach (var child in state.AdvancedEntries)
                {
                    var childItem = MakeItem(child, handlers);
                    advanced.DropDownItems.Add(childItem);
                    _advancedItems.Add((child.Id, childItem));
                }
            }

            _menu.Items.Add(item);
            _items.Add((entry.Id, item));
        }
    }

    private static ToolStripMenuItem MakeItem(TrayMenuEntry entry, Dictionary<TrayMenuId, EventHandler> handlers)
    {
        var item = new ToolStripMenuItem(entry.Text);
        if (handlers.TryGetValue(entry.Id, out var handler))
        {
            item.Click += handler;
        }

        if (entry.Bold)
        {
            item.Font = new Font(item.Font, FontStyle.Bold);
        }

        return item;
    }

    private void ApplyMenu(TrayMenuState state)
    {
        for (var i = 0; i < state.Entries.Count && i < _items.Count; i++)
        {
            Apply(state.Entries[i], _items[i].Item);
        }

        for (var i = 0; i < state.AdvancedEntries.Count && i < _advancedItems.Count; i++)
        {
            Apply(state.AdvancedEntries[i], _advancedItems[i].Item);
        }
    }

    private static void Apply(TrayMenuEntry entry, ToolStripItem item)
    {
        item.Visible = entry.Visible;
        if (item is ToolStripSeparator)
        {
            return;
        }

        item.Text = entry.Text;
        item.Enabled = entry.Enabled;
        item.ToolTipText = entry.ToolTip;
        if (item is ToolStripMenuItem menuItem)
        {
            menuItem.Checked = entry.Checked;
        }
    }

    private Dictionary<TrayMenuId, EventHandler> Handlers() => new()
    {
        [TrayMenuId.ReaddTransmission] = OnReaddTorrents,
        [TrayMenuId.InstallApps] = OnInstallApps,
        [TrayMenuId.Open] = (_, _) => OpenFromMenu(),
        [TrayMenuId.PauseResume] = OnPauseResume,
        [TrayMenuId.Restart] = (_, _) => _ = RestartHostAsync(),
        [TrayMenuId.CopyAddress] = OnCopyAddress,
        [TrayMenuId.AllowLan] = OnAllowLan,
        [TrayMenuId.OnlyThisPc] = OnOnlyThisPc,
        [TrayMenuId.ChangePort] = OnChangePort,
        [TrayMenuId.OpenData] = (_, _) => OpenFolder(AppSettings.Load().DataRoot),
        [TrayMenuId.OpenLogs] = (_, _) => OpenFolder(LogsFolder()),
        [TrayMenuId.StartWithWindows] = OnStartWithWindows,
        [TrayMenuId.Update] = OnUpdateItem,
        [TrayMenuId.Report] = (_, _) => OpenFolder(LogsFolder()),
        [TrayMenuId.RunAsService] = OnServiceMode,
        [TrayMenuId.LetAnyAddressIn] = OnLetAnyAddressIn,
        [TrayMenuId.Quit] = OnQuit
    };

    /// <summary>What a menu opening reads fresh: the sign-in tick and whatever is waiting to be installed or added back.</summary>
    private void RefreshLocalStatus()
    {
        try
        {
            var pending = Deluno.Contracts.PendingComponentInstalls.Read(ComponentPaths.DataRoot());
            var readd = TransmissionReadd.Pending(ComponentPaths.DataRoot()) is { } transmission && (transmission.Summary is null || transmission.Torrents.Count > 0);
            Change(s => s with
            {
                StartsWithWindows = SafeSignInEnabled(),
                InstallAppsText = pending is null ? null : PendingInstallRecorder.MenuText(pending, DateTimeOffset.UtcNow, DisplayNameOf),
                TransmissionReaddPending = readd
            });
        }
        catch
        {
            // The menu still opens; a line that could not be read stays as it was.
        }
    }

    private static TrayLanState LanStateOf(LanAccessStatus? lan) => lan?.State switch
    {
        LanAccessStates.Open => TrayLanState.Open,
        LanAccessStates.Blocked => TrayLanState.Blocked,
        _ => TrayLanState.NotAvailable
    };

    // ------------------------------------------------------------------ opening

    private string? LiveAddress => _server.ListeningPort is { } port ? $"http://localhost:{port}" : null;

    private void OpenFromIcon()
    {
        // A left click opens once, and the double-click that follows it does not open again.
        if (_openGate.ShouldOpen(DateTimeOffset.UtcNow))
        {
            OpenDeluno();
        }
    }

    private void OpenFromMenu() => OpenDeluno();

    private void OpenDeluno() => OpenAddress(string.Empty);

    private void OpenUpdatesPage() => OpenAddress("/system/updates");

    /// <summary>Opens a page at the port Deluno is really listening on, or says why it cannot yet. Only ever from a click.</summary>
    private void OpenAddress(string path)
    {
        if (_status.State == TrayState.Running && LiveAddress is { } address)
        {
            try
            {
                Process.Start(new ProcessStartInfo(address + path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Balloon("Deluno could not open your browser", ex.Message, ToolTipIcon.Warning, problem: true);
            }

            return;
        }

        var why = _status.State switch
        {
            TrayState.Starting => "Deluno is still starting. Click the icon again in a moment.",
            TrayState.Updating => "Deluno is updating. Click the icon again in a moment.",
            _ => "Deluno is not running. Choose Restart Deluno from the icon menu."
        };
        Balloon("Deluno can't open yet", why, ToolTipIcon.Info, problem: false);
    }

    private void ShowAlreadyRunning()
    {
        var text = LiveAddress is { } address ? address : "Deluno is still starting.";
        Balloon("Deluno is already running", text, ToolTipIcon.Info, OpenDeluno);
    }

    private static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch
        {
            // A folder that cannot be opened is not worth stopping the tray for.
        }
    }

    /// <summary>The application log, in the data folder. Report a problem opens it too, while the repository is private.</summary>
    private static string LogsFolder() => Path.Combine(AppSettings.Load().DataRoot, "logs");

    // ------------------------------------------------------------------ balloons

    /// <summary>Shows a balloon; a click runs <paramref name="onClick"/>. Information stays 10 seconds, a problem 30.</summary>
    private void Balloon(string title, string text, ToolTipIcon icon, Action? onClick = null, bool? problem = null)
    {
        _balloonClick = onClick;
        var isProblem = problem ?? icon is ToolTipIcon.Error or ToolTipIcon.Warning;
        _notify.ShowBalloonTip(TrayBalloons.DurationFor(isProblem), title, text, icon);
    }

    // ------------------------------------------------------------------ host

    private async Task StartInstalledComponentsAsync()
    {
        try
        {
            await MoveComponentFoldersAsync().ConfigureAwait(false);
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

    /// <summary>
    /// Before any client is started: component folders left in the install root move to the data root, and a Transmission
    /// folder a reinstall already removed is written again with a plain notice (#1177). Never stops the tray.
    /// </summary>
    private async Task MoveComponentFoldersAsync()
    {
        try
        {
            var dataRoot = ComponentPaths.DataRoot();
            var installed = new FileInstalledComponentRecord(FileInstalledComponentRecord.PathFor(dataRoot)).Read().Select(entry => entry.Kind).ToList();
            var spec = BundledComponents.Load().FirstOrDefault(s => s.Kind == ComponentStateMigration.Transmission);
            var daemonSummary = await RunningDaemonMigration.RunAsync(
                ComponentPaths.InstallRoot(), dataRoot,
                new WindowsTransmissionDaemonControl(spec),
                new TransmissionRpc(spec?.DefaultPort ?? 9091, BundledComponentCredentialStores.CreateForDataRoot(dataRoot).TryGet(ComponentStateMigration.Transmission)),
                new FileInstallLog(), CancellationToken.None).ConfigureAwait(false);
            if (daemonSummary is not null)
            {
                InvokeOnUiThread(() => Balloon("Transmission was moved", daemonSummary, ToolTipIcon.Info, problem: true));
            }

            var result = await ComponentStateMigration.RunAsync(
                ComponentPaths.InstallRoot(), dataRoot, installed,
                BundledComponentCredentialStores.CreateForDataRoot(dataRoot).TryGet(ComponentStateMigration.Transmission),
                new FileStartedComponentsRegistry(), spec?.DefaultPort ?? 9091, null, new FileInstallLog(), CancellationToken.None).ConfigureAwait(false);
            if (result.TransmissionFolderRecreated)
            {
                InvokeOnUiThread(() => Balloon("Transmission was reset", TransmissionReadd.NoticeText + " Choose \"Add Transmission's torrents back\" from the Deluno icon menu.", ToolTipIcon.Warning, () => OnReaddTorrents(null, EventArgs.Empty), problem: true));
            }
        }
        catch
        {
            // A folder that could not be moved is still where it was; the client starts from it.
        }
    }

    /// <summary>
    /// Apps still to install: ones GitHub's limit stopped at set-up, or ones asked for on the Companion Tools page.
    /// An ask opens the picker at once, once; a wait is only offered, with a balloon, when the limit has lifted.
    /// </summary>
    private void CheckPendingInstalls()
    {
        try
        {
            var dataRoot = ComponentPaths.DataRoot();
            if (_installPickerOpen || Deluno.Contracts.PendingComponentInstalls.Read(dataRoot) is not { } pending)
            {
                RefreshLocalStatus();
                return;
            }

            RefreshLocalStatus();
            if (pending.AskedByPerson)
            {
                Deluno.Contracts.PendingComponentInstalls.MarkAsked(dataRoot);
                ShowInstallPicker(pending.Kinds);
            }
            else if (PendingInstallRecorder.ReadyToOffer(pending, DateTimeOffset.UtcNow) && _offeredFor != (pending.RetryAfterUtc ?? DateTimeOffset.MinValue))
            {
                _offeredFor = pending.RetryAfterUtc ?? DateTimeOffset.MinValue;
                Balloon("GitHub's limit has lifted", "Choose \"Try again\" from the Deluno icon menu to install " + string.Join(", ", pending.Kinds.Select(DisplayNameOf)) + ".", ToolTipIcon.Info, () => OnInstallApps(null, EventArgs.Empty), problem: true);
            }
        }
        catch
        {
            // The menu item is still there to try by hand.
        }
    }

    private static string DisplayNameOf(string kind)
        => BundledComponents.Load().FirstOrDefault(spec => string.Equals(spec.Kind, kind, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? kind;

    private void OnInstallApps(object? sender, EventArgs e)
    {
        if (Deluno.Contracts.PendingComponentInstalls.Read(ComponentPaths.DataRoot()) is { } pending)
        {
            ShowInstallPicker(pending.Kinds);
        }
    }

    private void ShowInstallPicker(IReadOnlyCollection<string> kinds)
    {
        if (_installPickerOpen)
        {
            return;
        }

        _installPickerOpen = true;
        try
        {
            ComponentPickerDialog.ShowFor(kinds);
        }
        finally
        {
            _installPickerOpen = false;
            RefreshLocalStatus();
        }
    }

    private async void OnReaddTorrents(object? sender, EventArgs e)
    {
        var message = "Deluno could not add the torrents back.";
        try
        {
            var dataRoot = ComponentPaths.DataRoot();
            var spec = BundledComponents.Load().First(s => s.Kind == ComponentStateMigration.Transmission);
            var login = BundledComponentCredentialStores.CreateForDataRoot(dataRoot).TryGet(ComponentStateMigration.Transmission);
            message = await TransmissionReadd.RunAsync(dataRoot, new TransmissionRpc(spec.DefaultPort, login), ct => TransmissionDaemon.RestartAsync(spec, ct), CancellationToken.None);
        }
        catch (Exception ex)
        {
            message = $"Deluno could not add the torrents back: {ex.Message}";
        }

        MessageBox.Show(message, "Deluno", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>
    /// Every start of the web host goes through the one supervisor (#1256): the first start, Restart, Change port and a
    /// stop nobody asked for. It runs the start off the UI thread, watches its progress, and retries with a growing,
    /// jittered wait up to the start limit.
    /// </summary>
    private async Task StartServerAsync(StartReason reason)
    {
        Change(s => s with { State = TrayState.Starting, RestartNote = null, StartNote = null });
        var result = await _supervisor.StartAsync(reason);
        await HandleStartResultAsync(result, announce: reason == StartReason.FirstStart);
    }

    private async Task HandleUnexpectedStopAsync()
        => await HandleStartResultAsync(await _supervisor.StartAsync(StartReason.UnexpectedStop), announce: false);

    private async Task HandleStartResultAsync(StartResult result, bool announce)
    {
        switch (result.Outcome)
        {
            case StartOutcome.Started:
                InvokeOnUiThread(() => OnHostStarted(announce));
                await ReportBlockedUpdateAsync();
                break;
            case StartOutcome.ProcessRestarting:
                ExitForRestart();
                break;
            default:
                // Giving up has already been said (icon, menu and a balloon whose click is Restart); a cancelled
                // start is the person choosing Stop, Restart or Quit.
                break;
        }
    }

    /// <summary>The host could not be freed and the process was started again: let this one go.</summary>
    private void ExitForRestart()
    {
        _exitingForRestart = true;
        InvokeOnUiThread(() =>
        {
            try { _notify.Visible = false; } catch { /* the process is ending */ }
            Application.Exit();
        });
    }

    /// <summary>The tray's words for the phase a start is in ("opening the databases"), once per change of words.</summary>
    private void OnStartProgress(StartProgressSnapshot snapshot)
    {
        var note = snapshot.Phase == StartupPhase.Ready ? null : StartupPhases.Words(snapshot.Phase);
        if (string.Equals(note, _lastStartNote, StringComparison.Ordinal))
        {
            return;
        }

        _lastStartNote = note;
        InvokeOnUiThread(() =>
        {
            if (_status.State == TrayState.Starting)
            {
                Change(s => s with { StartNote = note });
            }
        });
    }

    /// <summary>
    /// Stops the host within a time limit. A stop that does not finish is forced (stop and dispose with a budget), and a
    /// host that still cannot be released restarts the process, so Restart can never wait for ever (#1256).
    /// </summary>
    private async Task StopHostAsync(string why)
    {
        try
        {
            await Task.Run(() => _server.StopAsync()).WaitAsync(StopBudget);
            return;
        }
        catch (TimeoutException)
        {
        }
        catch
        {
            return;
        }

        HookLog.Write($"stopping Deluno for {why} took longer than {StopBudget.TotalSeconds:0} seconds; releasing it.", "tray-startup.log");
        try
        {
            using var budget = new CancellationTokenSource(StopBudget);
            await Task.Run(() => _server.FreeAsync(budget.Token), budget.Token);
        }
        catch
        {
            HookLog.Write("Deluno could not be released; starting the program again.", "tray-startup.log");
            if (_relaunch.TryRestart($"stopping for {why} did not finish"))
            {
                ExitForRestart();
            }
        }
    }

    private static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// An automatic install on Quit or at start that a failed backup stopped (#1197) is said now, once: in Activity, in
    /// plain words, and as a tray notice.
    /// </summary>
    private async Task ReportBlockedUpdateAsync()
    {
        var note = BlockedUpdateNote.TryTake(BlockedUpdateNote.DefaultFolder());
        if (note is null)
        {
            return;
        }

        try
        {
            if (_server.Services is { } services)
            {
                using var scope = services.CreateScope();
                var feed = scope.ServiceProvider.GetRequiredService<Deluno.Jobs.Data.IActivityFeedRepository>();
                await feed.RecordActivityAsync(
                    "app.update",
                    note.Message,
                    System.Text.Json.JsonSerializer.Serialize(new { version = note.Version, reason = note.Reason, atUtc = note.AtUtc }),
                    null,
                    "system",
                    "app-update",
                    CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            HookLog.Write($"could not write the blocked-update line to Activity: {ex.Message}");
        }

        InvokeOnUiThread(() => Balloon("Deluno did not install the update", note.Message, ToolTipIcon.Warning, problem: true));
    }

    /// <summary>The host is listening: take its live address, follow its pushes and its update state.</summary>
    private void OnHostStarted(bool announce)
    {
        var port = _server.ListeningPort ?? _status.Port;
        Change(s => s with
        {
            State = TrayState.Running,
            RestartNote = null,
            StartNote = null,
            // The dot blinks until the server has said how its connections are.
            HealthKnown = false,
            Down = [],
            Port = port,
            Address = $"http://localhost:{port}",
            Lan = LanStateOf(_server.LanAccess)
        });

        AttachToHost();
        if (announce)
        {
            ShowReachableUrlsBalloon();
        }
    }

    private void AttachToHost()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (_updates is not null)
        {
            _updates.Changed -= OnUpdateChanged;
            _updates = null;
        }

        if (_server.Services is not { } services)
        {
            return;
        }

        _watcher = new TrayHealthWatcher(services, signals => InvokeOnUiThread(() => OnSignals(signals)));
        _watcher.Schedule();

        if (services.GetService(typeof(IUpdateOrchestrator)) is VelopackUpdateOrchestrator orchestrator)
        {
            _updates = orchestrator;
            orchestrator.Changed += OnUpdateChanged;
            OnUpdateChanged();
        }
    }

    private void OnSignals(TrayServerSignals signals)
        => Change(s => s with
        {
            HealthKnown = true,
            Down = signals.Down,
            HealthUnreadable = signals.HealthUnreadable,
            AutomationPaused = signals.AutomationPaused
        });

    private void OnUpdateChanged() => InvokeOnUiThread(() =>
    {
        if (_updates is null)
        {
            return;
        }

        try
        {
            var snapshot = _updates.Snapshot();
            var update = TrayUpdate.Describe(snapshot.IsInstalled, snapshot.State, snapshot.LatestVersion, snapshot.UpdateAvailable, snapshot.RestartRequired);
            // A new check clears the last one's problem; the manual check sets it again if it fails.
            Change(s => s with { Update = update, UpdateCheckProblem = snapshot.State == UpdateStates.Error ? s.UpdateCheckProblem : null });

            // An update that could not be applied leaves the host stopped: start it again.
            if (snapshot.State == UpdateStates.Error && _status.State == TrayState.Updating)
            {
                _ = RestartHostAsync();
            }

            AnnounceUpdate(update);
        }
        catch
        {
            // The menu keeps its last wording.
        }
    });

    /// <summary>One balloon per update: when it is ready, or (when updates are not downloaded by themselves) when it is found. A click opens System, Updates.</summary>
    private void AnnounceUpdate(TrayUpdate update)
    {
        if (!update.HasUpdate)
        {
            return;
        }

        var key = $"{update.Action}:{update.Version}";
        if (key == _announcedUpdate)
        {
            return;
        }

        if (update.Action == UpdateMenuAction.Download && AppSettings.Load().UpdateMode != UpdateModes.NotifyOnly)
        {
            // Deluno downloads it by itself; the balloon comes when it is ready.
            return;
        }

        _announcedUpdate = key;
        var version = update.Version is null ? string.Empty : $" (v{update.Version})";
        Balloon(
            update.Action == UpdateMenuAction.Restart ? "A Deluno update is ready" : "A Deluno update is available",
            $"Click here to open System > Updates{version}.",
            ToolTipIcon.Info,
            OpenUpdatesPage);
    }

    private void ShowReachableUrlsBalloon()
    {
        var urls = _server.ReachableUrls;
        if (urls.Count == 0)
        {
            return;
        }

        var lanUrls = urls.Where(u => !u.Contains("localhost", StringComparison.OrdinalIgnoreCase)).ToList();
        var title = "Deluno is running";
        var body = lanUrls.Count == 0
            ? $"Local only: {urls[0]}"
            : $"LAN: {lanUrls[0]}{(lanUrls.Count > 1 ? $" (+{lanUrls.Count - 1} more)" : string.Empty)}";

        // Other devices are turned away until the firewall lets them in (#623).
        var blocked = _server.LanAccess?.State == LanAccessStates.Blocked;
        if (blocked)
        {
            body += "  - Windows Firewall blocks other devices. Click here to allow them.";
        }

        Balloon(title, body, ToolTipIcon.Info, blocked ? () => OnAllowLan(null, EventArgs.Empty) : OpenDeluno, problem: blocked);
    }

    private async Task RestartHostAsync()
    {
        _supervisor.Cancel();
        Change(s => s with { State = TrayState.Starting, RestartNote = null, StartNote = null });
        await StopHostAsync("Restart");
        if (_exitingForRestart)
        {
            return;
        }

        await StartServerAsync(StartReason.Restart);
    }

    /// <summary>Stops the host cleanly before an update restarts the app, and keeps the supervisor from starting it again.</summary>
    private async Task StopHostForUpdateAsync()
    {
        _supervisor.Cancel();
        InvokeOnUiThread(() => Change(s => s with { State = TrayState.Updating }));
        await StopHostAsync("an update");
    }

    // ------------------------------------------------------------------ menu actions

    private async void OnPauseResume(object? sender, EventArgs e)
    {
        try
        {
            if (_server.Services is not { } services)
            {
                return;
            }

            // The Automation setting behind the Dashboard's "Automation is paused" notice and its Resume button.
            var resume = _status.AutomationPaused;
            var repository = services.GetRequiredService<IPlatformSettingsRepository>();
            var snapshot = await repository.SetGlobalAutomationEnabledAsync(resume, CancellationToken.None);
            await services.GetRequiredService<Deluno.Contracts.IRealtimeEventPublisher>().PublishEntityChangedAsync("Settings", "settings", CancellationToken.None);
            Change(s => s with { AutomationPaused = !snapshot.AutoStartJobs });
        }
        catch (Exception ex)
        {
            Balloon("Deluno could not change automation", ex.Message, ToolTipIcon.Warning, problem: true);
        }
    }

    private void OnCopyAddress(object? sender, EventArgs e)
    {
        // The address other devices use when the firewall lets them in; otherwise this PC's.
        var lan = _status.Lan == TrayLanState.Open
            ? _server.ReachableUrls.FirstOrDefault(u => !u.Contains("localhost", StringComparison.OrdinalIgnoreCase))
            : null;
        var address = lan?.TrimEnd('/') ?? LiveAddress;
        if (address is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(address);
            Balloon("Address copied", address, ToolTipIcon.Info, problem: false);
        }
        catch
        {
            Balloon("Deluno could not copy the address", address, ToolTipIcon.Warning, problem: true);
        }
    }

    /// <summary>Asks Windows, through its permission prompt, to let other devices reach Deluno, and says what happened (#623).</summary>
    private async void OnAllowLan(object? sender, EventArgs e)
    {
        if (_server.Firewall is null)
        {
            return;
        }

        Change(s => s with { LanBusy = true });
        var status = await _server.Firewall.AllowAsync(CancellationToken.None);
        await _server.RefreshLanAccessAsync(CancellationToken.None);
        InvokeOnUiThread(() =>
        {
            Change(s => s with { LanBusy = false, Lan = LanStateOf(_server.LanAccess) });
            Balloon(
                status.State == LanAccessStates.Open ? "Other devices can reach Deluno" : "Other devices still cannot reach Deluno",
                status.Message,
                status.State == LanAccessStates.Open ? ToolTipIcon.Info : ToolTipIcon.Warning);
        });
    }

    /// <summary>Removes Deluno's own firewall rule, so only this PC can open Deluno. Windows asks for permission.</summary>
    private async void OnOnlyThisPc(object? sender, EventArgs e)
    {
        if (_server.Firewall is not { } firewall)
        {
            return;
        }

        Change(s => s with { LanBusy = true });
        try
        {
            var hadOwnRule = firewall.HasOwnRule();
            var removed = hadOwnRule ? await firewall.RemoveAsync(CancellationToken.None) : null;
            await _server.RefreshLanAccessAsync(CancellationToken.None);
            InvokeOnUiThread(() =>
            {
                Change(s => s with { LanBusy = false, Lan = LanStateOf(_server.LanAccess) });
                if (!hadOwnRule)
                {
                    Balloon("Another firewall rule lets devices in", "Deluno did not add that rule, so it can't remove it. Remove it in Windows Defender Firewall to keep Deluno on this PC only.", ToolTipIcon.Warning);
                }
                else if (removed?.Declined == true)
                {
                    Balloon("Other devices can still reach Deluno", "Windows asked for permission to change the firewall, and it was not given.", ToolTipIcon.Warning);
                }
                else if (_server.LanAccess?.State == LanAccessStates.Open)
                {
                    Balloon("Other devices can still reach Deluno", "A different firewall rule still lets them in.", ToolTipIcon.Warning);
                }
                else
                {
                    Balloon("Only this PC can reach Deluno", "Other devices on your network are turned away. Choose Allow other devices on your network to let them in again.", ToolTipIcon.Info);
                }
            });
        }
        catch (Exception ex)
        {
            InvokeOnUiThread(() =>
            {
                Change(s => s with { LanBusy = false });
                Balloon("Deluno could not change the firewall", ex.Message, ToolTipIcon.Warning);
            });
        }
    }

    private async void OnChangePort(object? sender, EventArgs e)
    {
        var current = _server.ListeningPort ?? _status.Port;
        var chosen = PortDialog.Ask(current, PortChoice.IsInUse, _icon?.Icon);
        if (chosen is not { } port)
        {
            return;
        }

        var changer = new PortChanger(PortChoice.IsInUse, RestartOnPortAsync);
        var result = await changer.ChangeAsync(current, port);
        InvokeOnUiThread(() =>
        {
            switch (result.Outcome)
            {
                case PortChangeOutcome.Changed:
                    // The address is shown; nothing opens by itself. A click on the balloon opens it.
                    Balloon($"Deluno is now at port {result.Port}", $"http://localhost:{result.Port} - click to open. Bookmarks to the old address will stop working.", ToolTipIcon.Info, OpenDeluno);
                    break;
                case PortChangeOutcome.RolledBack:
                    Balloon("Deluno stayed on its old port", result.Message, ToolTipIcon.Warning, () => OpenFolder(LogsFolder()), problem: true);
                    break;
                case PortChangeOutcome.Failed:
                    Change(s => s with { State = TrayState.Error });
                    Balloon("Deluno stopped", result.Message, ToolTipIcon.Error, () => _ = RestartHostAsync());
                    break;
                case PortChangeOutcome.Refused:
                    Balloon("That port can't be used", result.Message, ToolTipIcon.Warning, problem: true);
                    break;
            }
        });
    }

    /// <summary>Saves the port and starts the host on it. Throws when it cannot start, so the change can roll back.</summary>
    private async Task RestartOnPortAsync(int port)
    {
        if (_exitingForRestart)
        {
            throw new InvalidOperationException("Deluno is starting the program again.");
        }

        _supervisor.Cancel();
        var settings = AppSettings.Load();
        settings.Port = port;
        settings.Save();
        // A --port start only lasted for one run; the person's choice here is the one that counts now.
        DelunoServer.PortOverride = null;

        // A rollback continues off the UI thread, so the icon is only touched through the UI thread.
        InvokeOnUiThread(() => Change(s => s with { State = TrayState.Starting, RestartNote = null, StartNote = null }));
        await StopHostAsync("a new port");
        // One try, no waiting: the change rolls back to the old port if this fails. A hang is still cut off.
        var result = await _supervisor.StartAsync(StartReason.PortChange, retry: false);
        switch (result.Outcome)
        {
            case StartOutcome.Started:
                InvokeOnUiThread(() => OnHostStarted(announce: false));
                break;
            case StartOutcome.ProcessRestarting:
                ExitForRestart();
                throw new InvalidOperationException("Deluno is starting the program again.");
            default:
                throw result.Exception ?? new InvalidOperationException(result.Failure ?? "Deluno could not start.");
        }
    }

    private void OnStartWithWindows(object? sender, EventArgs e)
    {
        try
        {
            var on = RunKeySignInEntry.Toggle(_signIn);
            Change(s => s with { StartsWithWindows = on });
        }
        catch (Exception ex)
        {
            Balloon("Deluno could not change Start with Windows", ex.Message, ToolTipIcon.Warning, problem: true);
        }
    }

    private async void OnUpdateItem(object? sender, EventArgs e)
    {
        try
        {
            switch (_status.Update.Action)
            {
                case UpdateMenuAction.OpenUpdatePage:
                    OpenUpdatesPage();
                    break;
                case UpdateMenuAction.Check when _updates is not null:
                {
                    var result = await _updates.CheckForUpdatesAsync(CancellationToken.None);
                    // A check that failed says so, plainly, here and in the status line (it used to go back to "Check for updates" silently).
                    var problem = TrayUpdate.CheckProblem(result.State, result.Message);
                    Change(s => s with { UpdateCheckProblem = problem });
                    if (problem is not null)
                    {
                        Balloon("Couldn't check for updates", problem, ToolTipIcon.Warning, OpenUpdatesPage, problem: true);
                    }

                    break;
                }
                case UpdateMenuAction.Download when _updates is not null:
                    await _updates.DownloadUpdatesAsync(CancellationToken.None);
                    break;
                case UpdateMenuAction.Restart when _updates is not null:
                    // The orchestrator stops the host cleanly (StopHostForUpdateAsync), then applies and restarts.
                    await _updates.ApplyAndRestartNowAsync(CancellationToken.None);
                    break;
            }
        }
        catch (Exception ex)
        {
            Balloon("Deluno could not update", ex.Message, ToolTipIcon.Warning, problem: true);
        }
    }

    /// <summary>
    /// The Windows tray recovery action for Allowed Hosts (#939): if every address Deluno can be reached on ends up
    /// refused, this needs no working web UI to fix -- it writes the same <c>Security:AllowedHosts:ForceDisabled</c>
    /// override docs/security-allowed-hosts.md documents, then restarts the embedded server so it takes effect.
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

        await RestartHostAsync();
        InvokeOnUiThread(() => Balloon(
            "Deluno answers on any address again",
            "Allowed Hosts is off until you turn it back on from System → Security.",
            ToolTipIcon.Info));
    }

    private async void OnServiceMode(object? sender, EventArgs e)
    {
        using var dialog = new ServiceModeDialog();
        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        Change(s => s with { State = TrayState.Updating });
        _supervisor.Cancel();
        await StopHostAsync("running as a service");
        ServiceManager.Apply(dialog.SelectedMode, dialog.ServiceUsername, dialog.ServicePassword);
        Application.Exit();
    }

    /// <summary>
    /// Quit stops Deluno and closes the icon, without freezing the icon while the host stops. A downloaded update is
    /// installed as the app exits.
    /// </summary>
    private async void OnQuit(object? sender, EventArgs e)
    {
        if (_quitting)
        {
            return;
        }

        _quitting = true;
        _supervisor.Cancel();
        Change(s => s with { State = TrayState.Updating });
        try
        {
            var updates = _updates ?? new VelopackUpdateOrchestrator(NullLogger<VelopackUpdateOrchestrator>.Instance);
            await updates.ApplyPendingOnExitAsync(_server.Services);
        }
        catch
        {
            // The update is still there at the next start.
        }

        try
        {
            await Task.Run(() => _server.StopAsync()).WaitAsync(StopBudget);
        }
        catch
        {
            // Quit anyway: the process ending stops what is left.
        }

        _notify.Visible = false;
        Application.Exit();
    }

    // ------------------------------------------------------------------ plumbing

    private void InvokeOnUiThread(Action action)
    {
        if (_ui.IsDisposed)
        {
            return;
        }

        if (_ui.InvokeRequired)
        {
            _ui.BeginInvoke(action);
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
            _watcher?.Dispose();
            _secondLaunch?.Dispose();
            _pendingInstallsTimer.Dispose();
            _blinkTimer.Dispose();
            _notify.Visible = false;
            _notify.Dispose();
            _icon?.Dispose();
            _server.Dispose();
            _ui.Dispose();
        }

        base.Dispose(disposing);
    }
}
