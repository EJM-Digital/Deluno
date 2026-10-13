using System.Diagnostics;
using System.ServiceProcess;
using Deluno.Contracts;

namespace Deluno.Tray;

public enum ServiceMode { Tray, ServiceLocalSystem, ServiceRunAsUser }

public static class ServiceManager
{
    private const string ServiceName = "Deluno";
    private static readonly string ExePath =
        Path.Combine(AppContext.BaseDirectory, "Deluno.exe");

    public static void Install(string[] args)
    {
        // args: --install-service [--username domain\user --password pass]
        string? username = null, password = null;

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--username", StringComparison.OrdinalIgnoreCase)) username = args[i + 1];
            if (args[i].Equals("--password", StringComparison.OrdinalIgnoreCase)) password = args[i + 1];
        }

        Apply(
            string.IsNullOrEmpty(username) ? ServiceMode.ServiceLocalSystem : ServiceMode.ServiceRunAsUser,
            username, password);
    }

    public static void Uninstall()
    {
        RemoveService();
        RemoveTrayStartup();
    }

    public static void Apply(ServiceMode mode, string? username, string? password)
    {
        RemoveService();
        RemoveTrayStartup();

        switch (mode)
        {
            case ServiceMode.Tray:
                SetTrayStartup();
                break;

            case ServiceMode.ServiceLocalSystem:
                CreateService(null, null);
                break;

            case ServiceMode.ServiceRunAsUser:
                if (string.IsNullOrWhiteSpace(username))
                    throw new ArgumentException("Username required for run-as-user service mode.");
                CreateService(username, password);
                break;
        }
    }

    public static bool IsInstalled()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            _ = sc.Status;
            return true;
        }
        catch { return false; }
    }

    public static ServiceControllerStatus? CurrentStatus()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            return sc.Status;
        }
        catch { return null; }
    }

    // ── Private helpers ────────────────────────────────────────────────────

    private static void CreateService(string? username, string? password)
    {
        var binPath = $"\"{ExePath}\" --service";

        // sc.exe is the simplest cross-version way to create a service with credentials
        var scArgs = $"create {ServiceName} " +
                     $"binPath= \"{binPath}\" " +
                     $"start= auto " +
                     $"DisplayName= \"Deluno Media Manager\"";

        RunSc(scArgs);
        RunSc($"description {ServiceName} \"Deluno media acquisition and management server\"");
        foreach (var failureArgs in FailureActionArguments(ServiceName))
        {
            RunSc(failureArgs);
        }

        if (!string.IsNullOrEmpty(username))
        {
            // Set the service logon account — requires a valid Windows account
            RunSc($"config {ServiceName} obj= \"{username}\" password= \"{password ?? ""}\"");
        }

        // Start immediately
        RunSc($"start {ServiceName}");
    }

    /// <summary>
    /// The sc.exe calls that make Windows start Deluno again after it stops
    /// itself (#1080): when a critical background service gives up, the host
    /// exits with a non-zero code, and without failure actions the installed
    /// service would stay stopped. <c>failureflag 1</c> makes the service
    /// control manager treat a stop with a non-zero exit code as a failure too.
    /// </summary>
    internal static IReadOnlyList<string> FailureActionArguments(string serviceName) =>
    [
        $"failure {serviceName} reset= 86400 actions= restart/60000/restart/60000/restart/60000",
        $"failureflag {serviceName} 1"
    ];

    /// <summary>The key of the Health note raised when the restart-on-failure settings cannot be set.</summary>
    public const string FailureActionsProblemKey = "startup:service-restart-on-failure";

    internal const string FailureActionsProblemMessage =
        "Windows would not let Deluno set its restart-on-failure settings, so if Deluno stops itself Windows will not start it again on its own. " +
        "Choose the Windows service option from the Deluno tray icon again and approve the Windows prompt to set them; this note goes away the next time Deluno starts with them set.";

    /// <summary>Runs one sc.exe command and returns its exit code, or null when it did not finish in time or could not start.</summary>
    internal interface IScRunner
    {
        int? Run(string arguments, TimeSpan timeout);
    }

    internal sealed class ProcessScRunner : IScRunner
    {
        public int? Run(string arguments, TimeSpan timeout)
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (p is null)
            {
                return null;
            }

            // Both streams are drained on their own so a full pipe never holds sc.exe up.
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { p.Kill(); } catch { /* best effort */ }
                return null;
            }

            Task.WaitAll([output, error], TimeSpan.FromSeconds(2));
            return p.ExitCode;
        }
    }

    /// <summary>
    /// Sets the restart-on-failure actions and returns null when they are all set, or a plain
    /// reason when Windows refused (a non-zero exit code) or sc.exe could not run or did not
    /// answer. Never throws.
    /// </summary>
    internal static string? SetFailureActions(IScRunner runner, TimeSpan timeout)
    {
        try
        {
            foreach (var failureArgs in FailureActionArguments(ServiceName))
            {
                var exit = runner.Run(failureArgs, timeout);
                if (exit is not 0)
                {
                    return exit is null
                        ? $"sc.exe {failureArgs.Split(' ')[0]} did not finish."
                        : $"sc.exe {failureArgs.Split(' ')[0]} exited with code {exit}.";
                }
            }

            return null;
        }
        catch (Exception exception)
        {
            return exception.Message;
        }
    }

    /// <summary>
    /// Runs when Deluno starts as the service: services installed by earlier builds were
    /// created without failure actions, so a Deluno that stopped itself would stay stopped
    /// (#1080). It runs on its own thread after Deluno is up, never on the start path, since
    /// the service manager allows a service about 30 seconds to start. The service has the
    /// rights to change its own settings when it runs as LocalSystem, so sc.exe is called
    /// directly with no permission prompt. When Windows refuses, a warning is logged and a
    /// Health note in plain words is raised (cleared by the next start that succeeds).
    /// </summary>
    internal static Task EnsureFailureActionsInBackground(Action<string> logWarning, IBackgroundProblemReporter? reporter, ServiceManager.IScRunner? runner = null, TimeSpan? timeout = null)
        => Task.Run(() =>
        {
            var problem = SetFailureActions(runner ?? new ServiceManager.ProcessScRunner(), timeout ?? TimeSpan.FromSeconds(10));
            if (problem is null)
            {
                reporter?.Clear(FailureActionsProblemKey);
                return;
            }

            logWarning($"Deluno could not set the service's restart-on-failure settings: {problem}");
            reporter?.Report(FailureActionsProblemKey, "Deluno may not restart itself", FailureActionsProblemMessage, problem);
        });

    internal static Task<string?> EnsureFailureActionsEarlyAsync(Action<string> logWarning, IScRunner? runner = null, TimeSpan? timeout = null)
        => Task.FromResult<string?>(null);

    private static void RemoveService()
    {
        if (!IsInstalled()) return;
        RunSc($"stop {ServiceName}");
        RunSc($"delete {ServiceName}");
    }

    private static void SetTrayStartup() => new RunKeySignInEntry(ExePath).Enable();

    private static void RemoveTrayStartup() => new RunKeySignInEntry(ExePath).Disable();

    private static void RunSc(string args)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = args,
            UseShellExecute = true,
            Verb = "runas",     // UAC elevation
            CreateNoWindow = true
        });
        p?.WaitForExit();
    }
}

/// <summary>
/// The Health row's "Try again" for the restart-on-failure set-up (#1080): runs the same
/// sc.exe calls again now and clears the problem when Windows accepts them.
/// </summary>
internal sealed class ServiceFailureActionsRetry(IBackgroundProblemReporter reporter, ServiceManager.IScRunner? runner = null, TimeSpan? timeout = null)
    : IBackgroundProblemRetry
{
    public string ProblemKey => ServiceManager.FailureActionsProblemKey;

    public Task<bool> RetryAsync(CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            var problem = ServiceManager.SetFailureActions(runner ?? new ServiceManager.ProcessScRunner(), timeout ?? TimeSpan.FromSeconds(10));
            if (problem is null)
            {
                reporter.Clear(ServiceManager.FailureActionsProblemKey);
                return true;
            }

            reporter.Report(
                ServiceManager.FailureActionsProblemKey,
                "Deluno may not restart itself",
                ServiceManager.FailureActionsProblemMessage,
                problem);
            return false;
        }, cancellationToken);
}
