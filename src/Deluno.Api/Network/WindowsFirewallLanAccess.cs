using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace Deluno.Api.Network;

/// <summary>
/// Deluno's Windows Firewall rule: one inbound allow rule for its port, on
/// every network profile (#623).
///
/// <para>Every profile, because Windows files a home network it has not been
/// told about as Public, and the rig's LAN is exactly that. A rule limited to
/// Private networks would change nothing there.</para>
///
/// <para>This is the one place that reads or writes the rule. The installer,
/// the tray and the System screen all go through it.</para>
/// </summary>
public sealed class WindowsFirewallLanAccess(int port, IFirewallCommandRunner? runner = null) : ILanAccess
{
    private readonly IFirewallCommandRunner _runner = runner ?? new NetshFirewallCommandRunner();

    public int Port => port;

    public string RuleName => RuleNameFor(port);

    public static string RuleNameFor(int port) => $"Deluno (port {port})";

    public async Task<LanAccessStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var probe = await _runner.RunAsync($"advfirewall firewall show rule name=\"{RuleName}\"", elevate: false, cancellationToken);
        return probe.ExitCode == 0
            ? new LanAccessStatus(
                LanAccessStates.Open,
                port,
                RuleName,
                $"Windows Firewall lets other devices on your network reach Deluno on port {port}.",
                CanAllow: false)
            : Blocked(null);
    }

    public async Task<LanAccessStatus> AllowAsync(CancellationToken cancellationToken)
    {
        var current = await GetStatusAsync(cancellationToken);
        if (current.State == LanAccessStates.Open)
        {
            return current;
        }

        var add = await _runner.RunAsync(
            $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={port} profile=any",
            elevate: !_runner.IsElevated,
            cancellationToken);

        if (add.Declined)
        {
            return Blocked("Windows asked for permission to change the firewall, and it was not given.");
        }

        var after = await GetStatusAsync(cancellationToken);
        return after.State == LanAccessStates.Open
            ? after
            : Blocked(string.IsNullOrWhiteSpace(add.Output)
                ? "Windows did not add the firewall rule."
                : $"Windows did not add the firewall rule: {add.Output.Trim()}");
    }

    /// <summary>Removes the rule. Run by the uninstaller, which leaves nothing behind.</summary>
    public Task<FirewallCommandResult> RemoveAsync(CancellationToken cancellationToken)
        => _runner.RunAsync($"advfirewall firewall delete rule name=\"{RuleName}\"", elevate: !_runner.IsElevated, cancellationToken);

    private LanAccessStatus Blocked(string? why) => new(
        LanAccessStates.Blocked,
        port,
        RuleName,
        why ?? $"Windows Firewall has no rule for Deluno's port {port}, so other devices on your network cannot open Deluno.",
        CanAllow: true);
}

public sealed record FirewallCommandResult(int ExitCode, string Output, bool Declined = false);

public interface IFirewallCommandRunner
{
    /// <summary>Whether this process can change the firewall without asking.</summary>
    bool IsElevated { get; }

    Task<FirewallCommandResult> RunAsync(string netshArguments, bool elevate, CancellationToken cancellationToken);
}

/// <summary>Runs <c>netsh</c>, asking Windows for permission first when told to elevate.</summary>
public sealed class NetshFirewallCommandRunner : IFirewallCommandRunner
{
    // ERROR_CANCELLED: the person said no to the permission prompt.
    private const int PermissionDeclined = 1223;

    public bool IsElevated
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public async Task<FirewallCommandResult> RunAsync(string netshArguments, bool elevate, CancellationToken cancellationToken)
    {
        try
        {
            if (elevate)
            {
                // A permission prompt cannot be combined with reading the
                // output, so the caller checks the rule afterwards instead.
                using var elevated = Process.Start(new ProcessStartInfo("netsh", netshArguments)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                if (elevated is null)
                {
                    return new FirewallCommandResult(-1, "Windows did not start netsh.");
                }

                await elevated.WaitForExitAsync(cancellationToken);
                return new FirewallCommandResult(elevated.ExitCode, string.Empty);
            }

            using var process = Process.Start(new ProcessStartInfo("netsh", netshArguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null)
            {
                return new FirewallCommandResult(-1, "Windows did not start netsh.");
            }

            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new FirewallCommandResult(process.ExitCode, (await output) + (await error));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == PermissionDeclined)
        {
            return new FirewallCommandResult(ex.NativeErrorCode, ex.Message, Declined: true);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new FirewallCommandResult(-1, ex.Message);
        }
    }
}
