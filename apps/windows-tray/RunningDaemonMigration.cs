using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Deluno.Api.Network;
using Deluno.Infrastructure.Processes;

namespace Deluno.Tray;

/// <summary>What Deluno needs to know about, and do to, a Transmission daemon that is already running.</summary>
public interface ITransmissionDaemonControl
{
    /// <summary>The full command line of every running Transmission daemon.</summary>
    Task<IReadOnlyList<string>> CommandLinesAsync(CancellationToken cancellationToken);

    /// <summary>Waits for the daemon to exit by itself; false if it was still running after <paramref name="timeout"/>.</summary>
    Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Ends any daemon still running (used only when it would not close by itself).</summary>
    Task KillAsync(CancellationToken cancellationToken);

    /// <summary>Starts the daemon the way the tray starts it, with its folder in the data root; true once it answers.</summary>
    Task<bool> StartAsync(CancellationToken cancellationToken);
}

public sealed class WindowsTransmissionDaemonControl(BundledComponentSpec? spec) : ITransmissionDaemonControl
{
    public async Task<IReadOnlyList<string>> CommandLinesAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (Process.GetProcessesByName("transmission-daemon").Length == 0)
            {
                return [];
            }

            var info = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command \"Get-CimInstance Win32_Process -Filter \\\"Name='transmission-daemon.exe'\\\" | ForEach-Object { $_.CommandLine }\"");
            // Read as it arrives, closed standard input, and a limit: PowerShell can stall on a busy machine (#1258).
            var result = await ChildProcess.RunAsync(info, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (!result.Started || result.TimedOut)
            {
                return [];
            }

            var text = result.Output;
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return [];
        }
    }

    public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var until = DateTime.UtcNow + timeout;
        while (Process.GetProcessesByName("transmission-daemon").Length > 0)
        {
            if (DateTime.UtcNow > until)
            {
                return false;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    public async Task KillAsync(CancellationToken cancellationToken)
    {
        foreach (var process in Process.GetProcessesByName("transmission-daemon"))
        {
            try
            {
                process.Kill();
                await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch
            {
                // Already gone.
            }
        }
    }

    public Task<bool> StartAsync(CancellationToken cancellationToken)
        => spec is null ? Task.FromResult(false) : TransmissionDaemon.StartAsync(spec, cancellationToken);
}

/// <summary>
/// A reinstall can replace the install folder while Transmission is still running with its settings folder inside it (#1177).
/// At start-up, before anything else, this closes that daemon cleanly, brings its folder over to the data root, starts it
/// again from there, and adds back any torrent that did not come back, so nobody has to do any of it by hand.
/// </summary>
public static class RunningDaemonMigration
{
    private static readonly Regex ConfigFolder = new(@"(?:^|\s)(?:-g|--config-dir)(?:\s+|=)(""[^""]+""|\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The <c>-g</c> folder in a daemon's command line, or null.</summary>
    public static string? ConfigFolderOf(string commandLine)
    {
        var match = ConfigFolder.Match(commandLine);
        return match.Success ? match.Groups[1].Value.Trim('"') : null;
    }

    /// <summary>Whether <paramref name="folder"/> is a component folder inside the install root. Null for the folder's name when it is not.</summary>
    private static string? NameUnderInstallRoot(string folder, string installRoot)
    {
        var components = Path.GetFullPath(Path.Combine(installRoot, "components")).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(folder).TrimEnd('\\', '/');
        return full.StartsWith(components, StringComparison.OrdinalIgnoreCase) && !full[components.Length..].Contains(Path.DirectorySeparatorChar)
            ? full[components.Length..]
            : null;
    }

    /// <summary>What was done, in plain words, or null when no daemon was running from the install folder.</summary>
    public static async Task<string?> RunAsync(
        string installRoot, string dataRoot, ITransmissionDaemonControl daemon, TransmissionRpc rpc, IInstallLog? log, CancellationToken cancellationToken)
    {
        string? old = null;
        foreach (var line in await daemon.CommandLinesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (ConfigFolderOf(line) is { } folder && NameUnderInstallRoot(folder, installRoot) == ComponentStateMigration.Transmission)
            {
                old = Path.GetFullPath(folder);
                break;
            }
        }

        if (old is null)
        {
            return null;
        }

        try
        {
            // Read what the daemon holds while it still can: its torrents and the folders it uses.
            var before = await rpc.ReadTorrentsAsync(cancellationToken).ConfigureAwait(false);

            // A reinstall may have deleted the folder under it; it needs somewhere to write on the way out.
            Directory.CreateDirectory(Path.Combine(old, "resume"));
            Directory.CreateDirectory(Path.Combine(old, "torrents"));

            var closedCleanly = await rpc.CallAsync("session-close", new Dictionary<string, object>(), cancellationToken).ConfigureAwait(false) is not null
                && await daemon.WaitForExitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (!closedCleanly)
            {
                await daemon.KillAsync(cancellationToken).ConfigureAwait(false);
            }

            var target = ComponentPaths.ComponentFolder(dataRoot, ComponentStateMigration.Transmission);
            ComponentStateMigration.MoveOrMerge(old, target, overwrite: true);
            log?.Write("Transmission was running from its old folder in the install folder; its settings were saved and moved to the data folder.");

            var started = await daemon.StartAsync(cancellationToken).ConfigureAwait(false);
            if (!started)
            {
                return Finish(dataRoot, "Transmission's settings were moved out of the install folder, but Deluno could not start it again. Start Transmission from its own icon.", [], log);
            }

            var missing = new List<TorrentToReadd>();
            if (before is { Count: > 0 })
            {
                var after = await ReadWhenReadyAsync(rpc, cancellationToken).ConfigureAwait(false);
                var have = (after ?? []).Select(t => t.MagnetLink).ToHashSet(StringComparer.OrdinalIgnoreCase);
                missing = before.Where(t => !have.Contains(t.MagnetLink)).ToList();
            }

            var added = 0;
            var stillMissing = new List<TorrentToReadd>();
            foreach (var torrent in missing)
            {
                if (await rpc.AddAsync(torrent, cancellationToken).ConfigureAwait(false))
                {
                    added++;
                }
                else
                {
                    stillMissing.Add(torrent);
                }
            }

            string summary;
            if (before is null)
            {
                summary = "Transmission was running from the old install folder, which a reinstall removed. Deluno moved its settings to the data folder and started it again, but could not read its torrent list first, so torrents it had before may need adding back.";
            }
            else if (stillMissing.Count > 0)
            {
                summary = $"Transmission was running from the old install folder, which a reinstall removed. Deluno moved its settings to the data folder and started it again; {stillMissing.Count} torrent{(stillMissing.Count == 1 ? "" : "s")} could not be added back and may need adding by hand.";
            }
            else if (added > 0)
            {
                summary = $"Transmission was running from the old install folder, which a reinstall removed. Deluno moved its settings to the data folder, started it again and added back {added} torrent{(added == 1 ? "" : "s")} that did not reload.";
            }
            else
            {
                summary = $"Transmission was running from the old install folder, which a reinstall removed. Deluno moved its settings to the data folder and started it again; all {before.Count} torrent{(before.Count == 1 ? "" : "s")} came back.";
            }

            return Finish(dataRoot, summary, stillMissing, log);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log?.Write($"Could not move Transmission's folder out of the install folder: {exception.Message}");
            return null;
        }
    }

    private static async Task<IReadOnlyList<TorrentToReadd>?> ReadWhenReadyAsync(TransmissionRpc rpc, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (await rpc.ReadTorrentsAsync(cancellationToken).ConfigureAwait(false) is { } list)
            {
                return list;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private static string Finish(string dataRoot, string summary, IReadOnlyList<TorrentToReadd> failed, IInstallLog? log)
    {
        log?.Write(summary);
        try
        {
            Directory.CreateDirectory(ComponentPaths.ComponentsFolder(dataRoot));
            // Torrents that could not be added keep the usual button; otherwise the notice only says what happened.
            var notice = new TransmissionReaddNotice(DateTimeOffset.UtcNow, false, failed, summary);
            File.WriteAllText(ComponentStateMigration.ReaddNoticePath(dataRoot), JsonSerializer.Serialize(notice));
        }
        catch (IOException)
        {
            // The log still has it.
        }

        return summary;
    }
}
