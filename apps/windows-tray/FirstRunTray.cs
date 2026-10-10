using System.Runtime.Versioning;

namespace Deluno.Tray;

/// <summary>
/// What the tray icon says while a clean install sets itself up (#1214). The first-run work (the Windows Firewall
/// question, Start with Windows, the app picker) runs inside Velopack's first-run hook, before the real tray exists, and
/// can take minutes. The standard says the icon appears at once and blinks while Deluno starts, so a small icon of its
/// own covers that gap: a blinking grey dot, "Deluno - setting up", and a menu with Open Deluno and Quit Deluno.
/// </summary>
public static class FirstRunTrayModel
{
    /// <summary>Set by the installer when it starts Deluno for the first time after an install (Velopack's first-run marker).</summary>
    public const string FirstRunVariable = "VELOPACK_FIRSTRUN";

    public const string HoverText = "Deluno - setting up";

    public const string OpenNoticeTitle = "Deluno can't open yet";

    public const string OpenNotice = "Deluno is still setting up. Click the icon again once setup has finished.";

    /// <summary>The same grey, blinking dot as any start-up.</summary>
    public static TrayIconSpec Icon { get; } = new(TrayDot.Starting, TrayMark.None);

    /// <summary>The menu: Open Deluno, then Quit Deluno, in the standard's wording.</summary>
    public static IReadOnlyList<TrayMenuEntry> Menu { get; } =
    [
        new(TrayMenuId.Open, "Open Deluno", Bold: true),
        new(TrayMenuId.Separator, string.Empty),
        new(TrayMenuId.Quit, "Quit Deluno", ToolTip: TrayMenuState.QuitToolTip)
    ];

    /// <summary>
    /// True when this start is the installer's first run of an installed copy: not a Velopack hook call, not a service
    /// command, and the installer's first-run marker is set.
    /// </summary>
    public static bool IsFirstRun(IReadOnlyCollection<string> args, Func<string, string?> environment)
    {
        if (args.Any(a => a.StartsWith("--veloapp-", StringComparison.OrdinalIgnoreCase)
                          || a.Equals("--service", StringComparison.OrdinalIgnoreCase)
                          || a.Equals("--install-service", StringComparison.OrdinalIgnoreCase)
                          || a.Equals("--uninstall-service", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(environment(FirstRunVariable));
    }
}

/// <summary>
/// The first-run icon. It lives on its own thread with its own message loop, because the first-run hook blocks the
/// main thread for long stretches (a netsh call, a modal picker) and the dot must keep blinking and the menu must keep
/// answering through all of it. It is taken down just after the real tray icon is up.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class FirstRunTray : IDisposable
{
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _thread;
    private Control? _ui;
    private NotifyIcon? _notify;
    private ContextMenuStrip? _menu;
    private System.Windows.Forms.Timer? _blink;
    private OwnedIcon? _icon;
    private bool _dotShown = true;
    private bool _disposed;

    private FirstRunTray()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Deluno first-run tray" };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    /// <summary>Shows the icon when this start is a first run; null otherwise, or when the icon could not be shown.</summary>
    public static FirstRunTray? StartIfFirstRun(string[] args)
    {
        if (!FirstRunTrayModel.IsFirstRun(args, Environment.GetEnvironmentVariable))
        {
            return null;
        }

        try
        {
            // Visual styles and DPI mode have to be set before the first window of the process exists.
            Program.EnsureWinFormsBootstrap();
            var tray = new FirstRunTray();
            tray._thread.Start();
            tray._ready.Wait(TimeSpan.FromSeconds(5));
            HookLog.Write("first run: the tray icon is shown while Deluno sets itself up.");
            return tray;
        }
        catch (Exception ex)
        {
            HookLog.Write($"first run: the tray icon could not be shown: {ex.Message}");
            return null;
        }
    }

    private void Run()
    {
        try
        {
            _ui = new Control();
            _ = _ui.Handle;

            _menu = new ContextMenuStrip { ShowItemToolTips = true };
            foreach (var entry in FirstRunTrayModel.Menu)
            {
                if (entry.Id == TrayMenuId.Separator)
                {
                    _menu.Items.Add(new ToolStripSeparator());
                    continue;
                }

                var item = new ToolStripMenuItem(entry.Text) { ToolTipText = entry.ToolTip };
                if (entry.Bold)
                {
                    item.Font = new Font(item.Font, FontStyle.Bold);
                }

                item.Click += entry.Id == TrayMenuId.Quit ? (_, _) => Quit() : (_, _) => ShowNotYetOpen();
                _menu.Items.Add(item);
            }

            _notify = new NotifyIcon { ContextMenuStrip = _menu, Text = FirstRunTrayModel.HoverText };
            Draw();
            _notify.Visible = true;
            _notify.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    ShowNotYetOpen();
                }
            };

            _blink = new System.Windows.Forms.Timer { Interval = 500 };
            _blink.Tick += (_, _) =>
            {
                _dotShown = !_dotShown;
                Draw();
            };
            _blink.Start();
        }
        catch (Exception ex)
        {
            HookLog.Write($"first run: the tray icon failed: {ex.Message}");
            return;
        }
        finally
        {
            _ready.Set();
        }

        Application.Run();
    }

    private void Draw()
    {
        var previous = _icon;
        var next = TrayIconRenderer.Render(FirstRunTrayModel.Icon, _dotShown);
        _notify!.Icon = next.Icon;
        _icon = next;
        previous?.Dispose();
    }

    private void ShowNotYetOpen()
        => _notify?.ShowBalloonTip(
            TrayBalloons.InformationMilliseconds,
            FirstRunTrayModel.OpenNoticeTitle,
            FirstRunTrayModel.OpenNotice,
            ToolTipIcon.Info);

    private void Quit()
    {
        HookLog.Write("first run: Quit chosen from the tray icon while Deluno was setting up.");
        TearDown();
        Environment.Exit(0);
    }

    /// <summary>Takes the icon down, on the thread that made it.</summary>
    private void TearDown()
    {
        try
        {
            _blink?.Stop();
            _blink?.Dispose();
            if (_notify is not null)
            {
                _notify.Visible = false;
                _notify.Dispose();
            }

            _menu?.Dispose();
            _icon?.Dispose();
            _ui?.Dispose();
        }
        catch
        {
            // The icon is going away either way.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_ui is { IsHandleCreated: true, IsDisposed: false })
            {
                _ui.BeginInvoke(() =>
                {
                    TearDown();
                    Application.ExitThread();
                });
                _thread.Join(TimeSpan.FromSeconds(3));
            }
        }
        catch
        {
            // Nothing more to do: the process ending removes the icon.
        }
    }
}
