using System.Diagnostics;
using Deluno.Tray;
using Velopack;

internal static class Program
{
    /// <summary>
    /// The real entry point, and deliberately synchronous.
    ///
    /// <para>Velopack's install, update and uninstall hooks have to run at the
    /// very start of the process, before anything else touches the app
    /// directory. An <c>async Task Main</c> looks like it does that in source
    /// and does not in fact: the compiler rewrites it into a state machine, so
    /// the hooks end up inside <c>MoveNext</c> behind the runtime's async
    /// setup. <c>vpk</c> warns about exactly this — <i>"VelopackApp.Run() was
    /// found in method MoveNext, which does not look like your application's
    /// entry point"</i> — and the failure it is warning about is an update that
    /// half-applies, which is the one path a packaged app cannot afford to get
    /// wrong.</para>
    ///
    /// <para>So the hooks run here, synchronously, and the rest of startup is
    /// handed to <see cref="RunAsync"/> afterwards.</para>
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        // Before anything else in the process writes: the standard handles are whatever the launcher left, and a pipe
        // nobody reads would stop whatever writes to it (#1258). Point all three at the null device.
        var standardHandles = StandardHandles.DetachToNullDevice();

        // Diagnostic only (#968): how long the OS took to create the process
        // and the CLR took to JIT and reach this line, before Velopack's hook
        // budget (30s install/update, 15s uninstall) even starts ticking on
        // its side. A clean-VM run once showed velopack.log logging "found
        // fast exit hook" and then nothing at all from Deluno.exe within 30s
        // -- this line, and the one line it writes, is what tells us whether
        // that gap was cold-start-before-Main or something inside the hook
        // body itself. Process.GetCurrentProcess().StartTime and a single
        // AppendAllText are the only cost paid here; neither touches
        // WinForms, ASP.NET, EF or any Deluno.* assembly.
        AppDomain.CurrentDomain.UnhandledException += HookUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, e) => HookLog.Write($"unobserved task exception: {e.Exception}");
        HookLog.Write($"Main starting: args=[{string.Join(' ', args)}].");
        HookLog.Write($"Standard handles at start: {standardHandles}.");

        // The install and update hooks do almost nothing here (the first-run steps run on the first start of a true first
        // install, never on a reinstall over existing data), yet Setup kills the hook after 30
        // seconds, and on a freshly booted PC busy with Windows servicing and
        // Defender, Velopack's own start-up before it reaches the callback took
        // longer than that (#968, clean-VM runs 10 and 11). So those two hooks
        // answer here, before Velopack is loaded at all.
        if (IsFastExitInstallHook(args))
        {
            HookLog.Write("after install/update: done; a true first install runs its first-run steps on its first start, a reinstall over existing data does not.");
            // A silent Setup over a running Deluno leaves it stopped: bring it back once Setup has exited (#1256).
            RelaunchAfterSetup.OnInstallOrUpdateHook();
            return 0;
        }

        LogColdStart();

        // The first run after an install does its work (firewall question, Start with Windows, the app picker) inside
        // the Velopack first-run hook below, which can take minutes. The tray icon is up before that begins and blinks
        // until the real one takes over (#1214).
        _firstRunTray = FirstRunTray.StartIfFirstRun(args);

        // Setup.exe and Update.exe run these per user, without administrator
        // rights, and kill them after 30 seconds (15 on uninstall). So the
        // firewall rule (#623) is only added here when the rights are already
        // there. The Windows permission prompt comes on the first run instead.
        // The install hooks exit the process when fired, so nothing after
        // this runs on install.
        try
        {
            VelopackApp.Build()
                .SetAutoApplyOnStartup(false)
                .OnAfterInstallFastCallback(_ => InstallHooks.OnAfterInstall())
                .OnAfterUpdateFastCallback(_ => InstallHooks.OnAfterInstall())
                .OnBeforeUninstallFastCallback(_ => InstallHooks.OnBeforeUninstall())
                .OnFirstRun(_ => InstallHooks.OnFirstRun())
                .Run();
        }
        catch (Exception ex)
        {
            // Nothing the hook does is needed for the install to be usable:
            // the firewall rule, picker and the rest run again on first start
            // (#968). So a failure here is logged, and a Velopack hook
            // invocation still exits 0 instead of failing the whole install.
            HookLog.Write($"Velopack startup/hook failed: {ex}");
            if (IsVelopackHookInvocation(args))
            {
                return 0;
            }

            throw;
        }

        try
        {
            RunAsync(args).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            TryLogStartupFailure(ex);
            throw;
        }
        finally
        {
            _firstRunTray?.Dispose();
        }
    }

    /// <summary>The icon shown while a clean install sets itself up; taken down once the real tray icon is up.</summary>
    private static FirstRunTray? _firstRunTray;

    private static async Task RunAsync(string[] args)
    {
        {
            // --port N override (diagnostic). Takes precedence over AppSettings.Port
            // for THIS run only — does not persist to deluno.json. Useful when the
            // configured port is blocked by another listener.
            DelunoServer.PortOverride = TryParsePortOverride(args);

            // Service mode
            if (args.Contains("--service", StringComparer.OrdinalIgnoreCase))
            {
                await ServiceHost.RunAsync(args);
                return;
            }

            // Service management
            if (args.Contains("--install-service", StringComparer.OrdinalIgnoreCase))
            {
                ServiceManager.Install(args);
                return;
            }

            if (args.Contains("--uninstall-service", StringComparer.OrdinalIgnoreCase))
            {
                ServiceManager.Uninstall();
                return;
            }

            // A copy started by the one that could not free its hung host waits for it to end first, so it can take the
            // single-instance lock instead of telling the dying copy "already running" (#1256).
            if (ProcessRelaunch.ParentToWaitFor(args) is { } parentPid)
            {
                WaitForProcessToEnd(parentPid, TimeSpan.FromSeconds(60));
            }

            // Tray app mode with single-instance guard
            using var mutex = new Mutex(true, @"Global\DelunoTrayApplication", out bool isFirstInstance);
            if (!isFirstInstance)
            {
                // The start that follows Setup (RelaunchAfterSetup) finds Deluno already running when Setup started it:
                // there is nothing to tell the person, and nothing to do.
                if (RelaunchAfterSetup.ShouldStepAside(args, () => true))
                {
                    HookLog.Write("Start after Setup: Deluno is already running, so this start has nothing to do.", TrayStartLog.FileName);
                    return;
                }

                // The copy that is running shows a balloon, "Deluno is already running", with the address.
                SecondLaunchSignal.Notify();
                return;
            }

            // Where this start has got to, from the first step: the backup below is the first one that can block.
            var progress = StartProgress.ForThisProcess();

            // An update downloaded earlier is applied now, before the host runs, so Deluno starts clean on the new version.
            if (TryApplyPendingUpdateAtStart(progress))
            {
                return;
            }

            // Left behind only when something other than an orderly exit ends this Deluno (Setup stops it before any hook of
            // the new version runs), which is how the install hook knows it was running (#1256).
            MarkRunning(RunningKind.Tray);

            EnsureWinFormsBootstrap();
            var tray = new TrayApplication(progress);
            // The real icon is up (it is shown in its constructor), so the first-run one can go without a gap.
            _firstRunTray?.Dispose();
            _firstRunTray = null;
            Application.Run(tray);
        }
    }

    /// <summary>Records that this process is the running Deluno, and clears the record when it exits in order.</summary>
    internal static void MarkRunning(RunningKind kind)
    {
        try
        {
            var marks = RunningMarks.ForThisMachine();
            marks.Set(kind, RunningMark.ThisProcess());
            AppDomain.CurrentDomain.ProcessExit += (_, _) => marks.Clear(kind, Environment.ProcessId);
        }
        catch (Exception ex)
        {
            HookLog.Write($"Could not record that Deluno is running: {ex.Message}", TrayStartLog.FileName);
        }
    }

    private static void WaitForProcessToEnd(int pid, TimeSpan limit)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.WaitForExit(limit);
        }
        catch
        {
            // Already gone, or not ours to look at.
        }
    }

    private static bool TryApplyPendingUpdateAtStart(StartProgress progress)
    {
        try
        {
            var logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Deluno", "logs");
            return new VelopackUpdateOrchestrator(Microsoft.Extensions.Logging.Abstractions.NullLogger<VelopackUpdateOrchestrator>.Instance).ApplyPendingAtStart(
                logs,
                progress,
                // The icon is up before the backup starts, so that wait is never a blank one.
                () => _firstRunTray ??= FirstRunTray.StartForBackup());
        }
        catch (Exception ex)
        {
            HookLog.Write($"applying a downloaded update at start failed: {ex.Message}");
            return false;
        }
    }

    private static bool _winFormsBootstrapped;

    /// <summary>
    /// EnableVisualStyles/SetCompatibleTextRenderingDefault/SetHighDpiMode
    /// must run before the first Win32Window (Form) is created anywhere in
    /// the process -- and must NOT run unconditionally at the top of Main:
    /// that was tried and reverted. Main also runs for Velopack's other
    /// lifecycle invocations (OnAfterInstall, OnBeforeUninstall, and an
    /// internal "--veloapp-install" fast-exit helper process, confirmed live
    /// via velopack.log: "Found fast exit hook: --veloapp-install" was the
    /// last line logged, then that process just sat there, never installing
    /// anything, for minutes past Velopack's own 30-second budget for this
    /// callback). None of those paths show a Form or need WinForms
    /// initialized at all, and initializing it unconditionally broke at
    /// least one of them.
    ///
    /// <para>So this runs lazily, guarded so it only executes once, from the
    /// two places that actually need it: <see cref="InstallHooks.OnFirstRun"/>,
    /// right before it can show the component picker (#640) -- a Form -- and
    /// <see cref="RunAsync"/>, right before showing the tray icon itself.
    /// Either can run first in a given process (a fresh install's first
    /// launch hits OnFirstRun first, in the same process, before RunAsync
    /// gets here); the guard makes calling it from both safe.</para>
    /// </summary>
    internal static void EnsureWinFormsBootstrap()
    {
        if (_winFormsBootstrapped)
        {
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        _winFormsBootstrapped = true;
    }

    private static int? TryParsePortOverride(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--port", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], out var p)
                && p > 0 && p < 65536)
            {
                return p;
            }
        }
        return null;
    }

    /// <summary>
    /// Writes one line to the same install-hook.log the hooks use, so a
    /// cold-start gap and a hook-body gap show up on the same timeline
    /// (#968). Best-effort: a failure here must never stop startup, and
    /// nothing before this point may depend on it running.
    /// </summary>
    private static void LogColdStart()
    {
        try
        {
            var elapsed = DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime();
            HookLog.Write($"Main entered {elapsed.TotalMilliseconds:F0}ms after process start.");
        }
        catch
        {
            // The process start time can be unreadable on a starved machine;
            // still leave the line.
            HookLog.Write("Main entered (start time unreadable).");
        }
    }

    /// <summary>True when Velopack started this process for one of its install lifecycle hooks.</summary>
    internal static bool IsVelopackHookInvocation(string[] args)
        => args.Any(a => a.StartsWith("--veloapp-", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Velopack's after-install and after-update hooks, which Deluno answers
    /// straight away. The uninstall hook still goes through Velopack, since it
    /// removes the firewall rule.
    /// </summary>
    internal static bool IsFastExitInstallHook(string[] args)
        => args.Any(a => a.Equals("--veloapp-install", StringComparison.OrdinalIgnoreCase)
                         || a.Equals("--veloapp-updated", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Logs any exception that would otherwise kill the process without a
    /// trace (#968): the hook process died after 2.5 s once and left no log.
    /// </summary>
    private static void HookUnhandledException(object? sender, UnhandledExceptionEventArgs e)
        => HookLog.Write($"unhandled exception (terminating={e.IsTerminating}): {e.ExceptionObject}");
    private static void TryLogStartupFailure(Exception ex)
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Deluno",
                "logs");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "tray-startup.log");
            File.AppendAllText(
                logPath,
                $"{DateTimeOffset.Now:O} startup failure{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Best-effort startup diagnostics only.
        }
    }
}
