using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Deluno.Tray;

/// <summary>What Setup has to bring back: nothing, the tray (which holds the server), or the Windows service.</summary>
internal enum RelaunchKind { None, Tray, Service }

/// <summary>
/// Deluno comes back after a Setup run over a running Deluno (#1256). Velopack stops the running Deluno before it runs any
/// hook, and a silent Setup starts nothing afterwards, so a silent over-install left Deluno stopped (the rc.30.27
/// golden-path run). Weir solved the same thing the same way.
///
/// The after-install and after-update hooks cannot start Deluno: Setup stops every process running from the install
/// folder again when the hook ends. So the hook starts a short PowerShell, which runs from System32, waits for Setup to
/// exit and then starts Deluno (the tray with <see cref="Argument"/>, or the service). That start leaves the work to a
/// Deluno that is already running, which is what a plain Setup, or the in-app "Restart to update", has just started, so
/// Deluno is never started twice.
///
/// Whether Deluno was running is read from <see cref="RunningMarks"/>, which a running Deluno keeps and an orderly exit
/// clears: Setup stopped it before the hook, so there is no process left to ask.
/// </summary>
internal static class RelaunchAfterSetup
{
    /// <summary>
    /// Added to the start that follows Setup: start Deluno, unless one is already running from this install. Whether
    /// Setup was silent is not asked: what matters is whether Setup started Deluno, and that has happened by the time
    /// Setup exits.
    /// </summary>
    internal const string Argument = "--after-setup";

    /// <summary>
    /// What the hook may spend. Velopack kills an install or update hook after 30 seconds; the hook only reads a mark and
    /// starts a helper, so this is generous, and anything slower is given up on rather than risking the install.
    /// </summary>
    internal static readonly TimeSpan HookBudget = TimeSpan.FromSeconds(10);

    /// <summary>How long the helper waits for Setup to exit; a Setup on a slow PC can take far longer than a couple of minutes.</summary>
    private static readonly TimeSpan SetupCeiling = TimeSpan.FromMinutes(30);

    /// <summary>The wait when Setup cannot be identified: long enough for a Setup to finish replacing the files.</summary>
    private static readonly TimeSpan UnknownSetupWait = TimeSpan.FromSeconds(20);

    private const string ServiceName = "Deluno";

    /// <summary>
    /// Arranges for Deluno to start once Setup has exited, when this install's Deluno was running and Setup ended it.
    /// Nothing is arranged otherwise: an install must not start a Deluno that was stopped, and a first install, or a copy
    /// run from anywhere but <paramref name="executable"/>'s install, has no running Deluno to bring back.
    /// </summary>
    internal static RelaunchKind Schedule(
        RunningMarks marks,
        string executable,
        Func<RunningMark, bool> isRunning,
        Func<bool> serviceInstalled,
        Func<int?> setupProcessId,
        Func<int, RunningMark?> killedBySetup,
        string logPath,
        Action<ProcessStartInfo> start,
        Action<string> log)
    {
        // The tray's mark is taken in either case, so a second hook of the same Setup finds nothing left to answer.
        var tray = marks.TakeLeftBy(RunningKind.Tray, executable, isRunning);
        var service = serviceInstalled() ? marks.TakeLeftBy(RunningKind.Service, executable, isRunning) : (RunningMark?)null;

        var setup = setupProcessId();
        if (setup is not > 0)
        {
            setup = null;
        }

        var kind = service is not null ? RelaunchKind.Service : tray is not null ? RelaunchKind.Tray : RelaunchKind.None;
        var target = (service ?? tray)?.Executable;
        if (kind == RelaunchKind.None && setup is { } setupId && killedBySetup(setupId) is { } killed && !isRunning(killed))
        {
            // The Deluno that was running did not leave a mark (it is a build from before the marks), but Velopack
            // logged that this Setup ended it. That is the same fact, from the other side.
            kind = serviceInstalled() ? RelaunchKind.Service : RelaunchKind.Tray;
            target = killed.Executable;
            log($"Velopack's log says Setup (pid {setupId.ToString(CultureInfo.InvariantCulture)}) ended Deluno (pid {killed.ProcessId.ToString(CultureInfo.InvariantCulture)}), which left no record that it was running.");
        }

        if (kind == RelaunchKind.None || target is null)
        {
            log("Deluno was not running from this install before Setup, so it is left stopped.");
            return RelaunchKind.None;
        }

        var what = kind == RelaunchKind.Service ? "The Deluno service" : "Deluno";
        if (setup is null)
        {
            log("Setup could not be identified, so Deluno starts again after a short wait.");
        }

        try
        {
            start(HelperStart(kind, setup, target, logPath));
            var when = setup is { } id
                ? $"once Setup (pid {id.ToString(CultureInfo.InvariantCulture)}) has exited"
                : $"in {UnknownSetupWait.TotalSeconds:0} seconds";
            var unless = kind == RelaunchKind.Service ? "unless Windows has started it" : "unless Setup starts it";
            log($"{what} was running before this install. It starts again {when}, {unless}.");
            return kind;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            log($"{what} was running before this install, but it could not be started again: {ex.Message}");
            return RelaunchKind.None;
        }
    }

    /// <summary>
    /// Runs the work inside the hook's budget, on its own thread. Returns false when it ran out of time or failed; the
    /// install goes on either way, since a missing restart costs far less than a failed install.
    /// </summary>
    internal static bool RunWithinBudget(Action work, TimeSpan budget, TimeProvider time, Action<string> log)
    {
        try
        {
            var task = Task.Run(work);
            using var stop = new CancellationTokenSource();
            var delay = Task.Delay(budget, time, stop.Token);
            if (Task.WhenAny(task, delay).GetAwaiter().GetResult() != task)
            {
                log($"Deluno could not arrange its restart within the install hook's budget of {budget.TotalSeconds:0} seconds, so it is not started again.");
                return false;
            }

            stop.Cancel();
            task.GetAwaiter().GetResult();
            return true;
        }
        catch (Exception ex)
        {
            log($"Deluno could not arrange its restart after Setup: {ex.Message}");
            return false;
        }
    }

    /// <summary>The hook itself: reads the marks of the real machine and starts the helper, inside the budget.</summary>
    internal static void OnInstallOrUpdateHook()
    {
        var executable = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Deluno.exe");
        var logPath = Path.Combine(HookLog.PrimaryFolder, "install-hook.log");
        RunWithinBudget(
            () => Schedule(
                RunningMarks.ForThisMachine(),
                executable,
                mark => mark.IsRunning(),
                ServiceManager.IsInstalled,
                ParentProcess.Id,
                setup => VelopackKillEvidence.Find(VelopackKillEvidence.LogTails(executable), setup, executable),
                logPath,
                info => Process.Start(info)?.Dispose(),
                LogDecision),
            HookBudget,
            TimeProvider.System,
            LogDecision);
    }

    /// <summary>Each decision goes to the install hook log and to the start log, where a person looks when Deluno did not come back.</summary>
    internal static void LogDecision(string message)
    {
        HookLog.Write(message);
        HookLog.Write(message, TrayStartLog.FileName);
    }

    /// <summary>
    /// Whether this start, which follows Setup, has nothing to do because Deluno is already running. The question is asked
    /// only for a start that carries <see cref="Argument"/>.
    /// </summary>
    internal static bool ShouldStepAside(IEnumerable<string> args, Func<bool> anotherRuns) =>
        args.Contains(Argument, StringComparer.OrdinalIgnoreCase) && anotherRuns();

    /// <summary>
    /// The PowerShell that waits for Setup and then starts Deluno, with no window. It is started through the shell, which
    /// hands it no handles: a caller that captures Setup's output waits for every holder of Setup's pipes to close them, and
    /// this PowerShell and the Deluno it starts outlive Setup.
    /// </summary>
    internal static ProcessStartInfo HelperStart(RelaunchKind kind, int? setupProcessId, string executable, string logPath)
    {
        var start = new ProcessStartInfo(PowerShellPath())
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command", HelperScript(kind, setupProcessId, executable, logPath) })
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }

    /// <summary>
    /// The helper's script. A Setup that has already exited is not waited for, and one that outlasts the ceiling is given
    /// up on rather than waited for for ever. Without a Setup to wait for it waits a fixed while instead. Before it starts
    /// anything it looks again: a plain Setup, or the in-app update, has started Deluno by the time Setup exits.
    /// </summary>
    internal static string HelperScript(RelaunchKind kind, int? setupProcessId, string executable, string logPath)
    {
        var exe = Quote(executable);
        var startLog = Quote(Path.Combine(Path.GetDirectoryName(logPath) ?? string.Empty, TrayStartLog.FileName));
        var wait = setupProcessId is { } id and > 0
            ? $"$setup = Get-Process -Id {id.ToString(CultureInfo.InvariantCulture)} -ErrorAction SilentlyContinue; " +
              $"if ($setup -and -not $setup.WaitForExit({(int)SetupCeiling.TotalMilliseconds})) {{ L 'Setup did not exit in time, so Deluno is not started again.'; exit 1 }}; "
            : $"Start-Sleep -Seconds {(int)UnknownSetupWait.TotalSeconds}; ";

        var act = kind == RelaunchKind.Service
            ? "$s = Get-Service -Name '" + ServiceName + "' -ErrorAction SilentlyContinue; " +
              "if (-not $s) { L 'The Deluno service is not installed any more, so it is not started.' } " +
              "elseif ($s.Status -eq 'Running' -or $s.Status -eq 'StartPending') { L 'The Deluno service is already running, so it is not started again.' } " +
              "else { try { Start-Service -Name '" + ServiceName + "' -ErrorAction Stop; L 'Setup has exited: started the Deluno service.' } " +
              "catch { L ('Windows would not let this user start the Deluno service: ' + $_.Exception.Message + ' Windows starts it again by its own recovery settings.') } }"
            : $"$name = [IO.Path]::GetFileNameWithoutExtension({exe}); " +
              $"$up = @(Get-Process -Name $name -ErrorAction SilentlyContinue | Where-Object {{ try {{ $_.MainModule.FileName -eq {exe} }} catch {{ $false }} }}); " +
              "if ($up.Count -gt 0) { L 'Deluno is already running (Setup started it), so it is not started again.'; exit 0 }; " +
              "Remove-Item -LiteralPath Env:\\VELOPACK_FIRSTRUN -ErrorAction SilentlyContinue; " +
              $"Start-Process -FilePath {exe} -ArgumentList '{Argument}'; " +
              "L 'Setup has exited: started Deluno.'";

        return
            "$ErrorActionPreference = 'Continue'; " +
            $"function L($m) {{ $t = (Get-Date -Format o) + ' ' + $m; foreach ($f in @({Quote(logPath)}, {startLog})) {{ try {{ Add-Content -LiteralPath $f -Value $t -Encoding UTF8 }} catch {{ }} }} }}; " +
            wait + act;
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string PowerShellPath() =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
}

/// <summary>
/// The process that started this one. An installer hook is started by Setup, and the hook needs Setup's id to wait for its
/// exit; .NET has no way to ask for a parent.
/// </summary>
internal static class ParentProcess
{
    private const int ProcessBasicInformationClass = 0;

    // Classic DllImport: the source-generated form needs <AllowUnsafeBlocks>.
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        nint processHandle,
        int informationClass,
        ref ProcessBasicInformation information,
        int informationLength,
        out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    /// <summary>The id of the process that started this one, or null when Windows does not say.</summary>
    internal static int? Id()
    {
        using var self = Process.GetCurrentProcess();
        var information = new ProcessBasicInformation();
        var status = NtQueryInformationProcess(
            self.Handle,
            ProcessBasicInformationClass,
            ref information,
            Marshal.SizeOf<ProcessBasicInformation>(),
            out _);
        return status == 0 ? (int)information.InheritedFromUniqueProcessId : null;
    }
}

/// <summary>
/// A reinstall over an existing Deluno is not a first run (#1256). Setup over an existing Deluno runs the same install hook
/// a new one does, and a Setup that is not silent starts the app with Velopack's first-run marker, so the first-run steps
/// (the component picker, the Windows Firewall question, Start with Windows) would be asked again on every reinstall. They
/// are asked only when no settings and no data exist.
/// </summary>
internal static class ExistingInstall
{
    /// <summary>Whether settings or data already exist, from the places Deluno keeps them.</summary>
    internal static bool Detect(AppSettingsPathState state, Func<string, bool> fileExists, Func<string, bool> folderHoldsData)
    {
        if (state.PrimaryConfigExists || state.LegacyConfigExists)
        {
            return true;
        }

        // Where older builds kept the settings, inside the install folder: <local app data>\Deluno\config\deluno.json.
        var localAppData = Path.GetDirectoryName(Path.GetDirectoryName(state.PrimaryConfigPath));
        if (!string.IsNullOrEmpty(localAppData) && fileExists(Path.Combine(localAppData, "Deluno", "config", "deluno.json")))
        {
            return true;
        }

        return folderHoldsData(state.DefaultDataRoot) || folderHoldsData(state.LegacyDefaultDataRoot);
    }

    /// <summary>The same question for this machine.</summary>
    internal static bool DetectThisMachine()
    {
        try
        {
            return Detect(AppSettings.InspectPathState(), File.Exists, FolderHoldsData);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // When it cannot be told, ask nothing: a question wrongly skipped can be asked from the tray, a repeat cannot be undone.
            return true;
        }
    }

    private static bool FolderHoldsData(string folder)
    {
        try
        {
            return Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Runs the first-run steps only when this is a first install; logs the decision either way. Returns whether they ran.</summary>
    internal static bool RunFirstRunSteps(bool existingInstall, Action steps, Action<string> log)
    {
        if (existingInstall)
        {
            log("first run: this is a reinstall over an existing Deluno (its settings or data are there), so the component picker, the firewall question and the Start with Windows question are not asked again.");
            return false;
        }

        steps();
        return true;
    }
}

/// <summary>
/// What Velopack wrote down about the Deluno it ended. A Deluno from before the running marks leaves no mark, so on the
/// first update from such a build the mark cannot say it was running. Setup and Update.exe log every process they end, in
/// the shared Velopack log, as <c>[setup:7604] [22:34:05] [WARN] Killing process: C:\...\Deluno\current\Deluno.exe (3036)</c>
/// (recorded from a real Setup over a running Deluno), and the first number is the id of the Setup that did it, which is
/// the id the hook sees as its parent. So the line is tied to this very Setup, with no clock to compare.
/// </summary>
internal static partial class VelopackKillEvidence
{
    private const int TailBytes = 512 * 1024;

    [System.Text.RegularExpressions.GeneratedRegex(@"^\[(?:setup|update):(?<setup>\d+)\] \[[^\]]*\] \[[A-Z]+\] Killing process: (?<path>.+) \((?<pid>\d+)\)\s*$")]
    private static partial System.Text.RegularExpressions.Regex KillLine();

    /// <summary>The last Deluno.exe of this install that the Setup with this id ended, from the lines of the log; null when none.</summary>
    internal static RunningMark? Find(IEnumerable<string> lines, int setupProcessId, string executable)
    {
        RunningMark? found = null;
        foreach (var line in lines)
        {
            var match = KillLine().Match(line);
            if (!match.Success
                || !int.TryParse(match.Groups["setup"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var setup)
                || setup != setupProcessId
                || !int.TryParse(match.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            {
                continue;
            }

            var path = match.Groups["path"].Value;
            if (RunningMarks.SameInstall(path, executable))
            {
                found = new RunningMark(pid, 0, path);
            }
        }

        return found;
    }

    /// <summary>The ends of the logs Velopack writes: the shared one in the local app data folder, and the install folder's own.</summary>
    internal static IEnumerable<string> LogTails(string executable)
    {
        var lines = new List<string>();
        var shared = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "velopack", "velopack.log");
        var root = Path.GetDirectoryName(Path.GetDirectoryName(executable));
        var candidates = root is null ? [shared] : new[] { shared, Path.Combine(root, "velopack.log") };
        foreach (var path in candidates)
        {
            lines.AddRange(Tail(path));
        }

        return lines;
    }

    private static IEnumerable<string> Tail(string path)
    {
        try
        {
            // Setup still holds the log open while the hook runs, so it is read shared.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > TailBytes)
            {
                stream.Seek(-TailBytes, SeekOrigin.End);
            }

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
