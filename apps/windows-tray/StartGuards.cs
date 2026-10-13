using System.Globalization;
using System.Text.Json;
using Deluno.Infrastructure.Storage;

namespace Deluno.Tray;

/// <summary>
/// Watches a start's progress and says when it has made none for the window of the phase it is in (#1256). It does not
/// poll: it sleeps until the moment the window would end, and wakes early whenever progress moves, so a phase with a
/// shorter window than the last one is judged on time too. Used by the supervisor for every start and by the Windows
/// service, which has no supervisor.
/// </summary>
public static class StartHangWatch
{
    /// <returns>
    /// The snapshot of the step that was stuck when its window ran out; null when the start reached Ready or
    /// <paramref name="stop"/> was cancelled.
    /// </returns>
    public static async Task<StartProgressSnapshot?> WaitForHangAsync(
        IStartProgressSource source,
        StartWindows windows,
        TimeProvider time,
        CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var snapshot = source.Current;
            if (snapshot.Phase == StartupPhase.Ready)
            {
                return null;
            }

            var remaining = snapshot.AtUtc + windows.For(snapshot.Phase) - time.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return snapshot;
            }

            var moved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnChanged(StartProgressSnapshot _) => moved.TrySetResult();
            source.Changed += OnChanged;
            try
            {
                // Progress may have moved between reading it and listening for it.
                if (!ReferenceEquals(source.Current, snapshot))
                {
                    continue;
                }

                using var sleep = CancellationTokenSource.CreateLinkedTokenSource(stop);
                var timer = Task.Delay(remaining, time, sleep.Token);
                await Task.WhenAny(moved.Task, timer).ConfigureAwait(false);
                sleep.Cancel();
            }
            finally
            {
                source.Changed -= OnChanged;
            }
        }

        return null;
    }
}

public interface IProcessLauncher
{
    void Launch(string exePath, string arguments);
}

/// <summary>
/// Starts Deluno.exe again when the in-process host cannot be freed (#1256): the tray and the web server are one process,
/// so a server that will not let go of its databases or port cannot be killed without ending the tray with it. The new
/// copy is told which process to wait for (it cannot take the single-instance lock until this one has gone). A loop guard,
/// kept in a file so it survives the relaunch, allows <see cref="MaxRelaunches"/> relaunches in <see cref="GuardWindow"/>;
/// beyond that the supervisor gives up with a message instead of starting over for ever.
/// </summary>
public sealed class ProcessRelaunch(
    string guardFolder,
    TimeProvider time,
    IProcessLauncher launcher,
    int pid,
    string? exePath,
    ISupervisorLog log) : IProcessRestarter
{
    public const string AfterRestartArgument = "--after-restart";
    public const string GuardFileName = "restart-guard.json";
    public const int MaxRelaunches = 2;
    public static readonly TimeSpan GuardWindow = TimeSpan.FromMinutes(30);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string GuardPath => Path.Combine(guardFolder, GuardFileName);

    public bool TryRestart(string reason)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            log.Error($"Deluno could not restart the program ({reason}): its own path is unknown.");
            return false;
        }

        var now = time.GetUtcNow();
        var recent = ReadRecent(now);
        if (recent.Count >= MaxRelaunches)
        {
            log.Error(string.Create(
                CultureInfo.InvariantCulture,
                $"Deluno did not restart the program ({reason}): it has already done so {recent.Count} times in the last {GuardWindow.TotalMinutes:0} minutes, and starting over again would only repeat it."));
            return false;
        }

        try
        {
            launcher.Launch(exePath, $"{AfterRestartArgument} {pid.ToString(CultureInfo.InvariantCulture)}");
        }
        catch (Exception ex)
        {
            log.Error($"Deluno could not restart the program ({reason}): {ex.Message}");
            return false;
        }

        recent.Add(now);
        WriteRecent(recent);
        log.Warning($"Deluno is starting the program again ({reason}). The new copy waits for this one (process {pid}) to end.");
        return true;
    }

    /// <summary>The process a relaunched Deluno must wait for before it takes the single-instance lock, from its arguments.</summary>
    public static int? ParentToWaitFor(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], AfterRestartArgument, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var parent)
                && parent > 0)
            {
                return parent;
            }
        }

        return null;
    }

    private List<DateTimeOffset> ReadRecent(DateTimeOffset now)
    {
        try
        {
            if (File.Exists(GuardPath))
            {
                var all = JsonSerializer.Deserialize<List<DateTimeOffset>>(File.ReadAllText(GuardPath), Json) ?? [];
                return all.Where(at => now - at < GuardWindow && at <= now + TimeSpan.FromMinutes(1)).ToList();
            }
        }
        catch
        {
            // A damaged guard file must not stop a restart; it is written fresh below.
        }

        return [];
    }

    private void WriteRecent(List<DateTimeOffset> recent)
    {
        try
        {
            Directory.CreateDirectory(guardFolder);
            File.WriteAllText(GuardPath, JsonSerializer.Serialize(recent, Json));
        }
        catch
        {
            // If the guard cannot be kept, the relaunch has already happened.
        }
    }
}

/// <summary>
/// Windows service mode (#1256). The service manager sees the service as running as soon as the host's lifetime has
/// answered, which is before the databases are opened; so a hung start is invisible to it. This watches the start the same
/// way the supervisor does and, when it hangs, ends the process with a non-zero exit so the service manager's recovery
/// settings (restart on failure) act. A start that fails outright exits non-zero too.
/// </summary>
public static class ServiceStartGuard
{
    public const int FailedExitCode = 1;
    public const int HungExitCode = 3;

    public static async Task WatchAsync(
        IStartProgressSource progress,
        StartWindows windows,
        TimeProvider time,
        ISupervisorLog log,
        Func<string> diagnostics,
        Action<int> exit,
        CancellationToken stop)
    {
        var hung = await StartHangWatch.WaitForHangAsync(progress, windows, time, stop).ConfigureAwait(false);
        if (hung is null)
        {
            return;
        }

        var detail = string.IsNullOrWhiteSpace(hung.Detail) ? "no step named" : hung.Detail;
        string where;
        try
        {
            where = diagnostics();
        }
        catch (Exception ex)
        {
            where = $"(the process could not be looked at: {ex.Message})";
        }

        log.Error(string.Create(
            CultureInfo.InvariantCulture,
            $"Deluno's start made no progress for {windows.For(hung.Phase).TotalMinutes:0} minutes while {StartupPhases.Words(hung.Phase)} (last step: {detail}, which began at {hung.AtUtc:u}; process {hung.Pid}). Ending the process with exit code {HungExitCode} so the service manager restarts it. {where}"));
        exit(HungExitCode);
    }

    public static void OnStartFailed(Exception failure, ISupervisorLog log, Action<int> exit)
    {
        log.Error($"Deluno could not start: {failure.Message}. Ending the process with exit code {FailedExitCode} so the service manager restarts it.");
        log.Error("Details of the failed start: " + failure);
        exit(FailedExitCode);
    }
}
