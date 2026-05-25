using System.Diagnostics;

namespace Deluno.Tray;

/// <summary>
/// Adds a Windows Defender Firewall inbound allow rule for Deluno's listen
/// port, so LAN devices can reach the web UI. Idempotent — if a rule with
/// the same name already exists, leaves it alone.
///
/// Requires admin to add a rule. If the current process isn't elevated,
/// <see cref="EnsureInboundAllowAsync"/> returns <see cref="FirewallRuleStatus.RequiresElevation"/>
/// without throwing, and the tray surfaces a notification telling the user
/// what to do.
/// </summary>
internal static class WindowsFirewallService
{
    private const string RuleNamePrefix = "Deluno (port ";

    public static async Task<FirewallRuleStatus> EnsureInboundAllowAsync(int port, CancellationToken ct = default)
    {
        var ruleName = $"{RuleNamePrefix}{port})";

        if (await RuleExistsAsync(ruleName, ct).ConfigureAwait(false))
            return new FirewallRuleStatus(FirewallRuleState.AlreadyExists, ruleName, null);

        var addResult = await RunNetshAsync(
            $"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=TCP localport={port}",
            ct).ConfigureAwait(false);

        if (addResult.ExitCode == 0)
            return new FirewallRuleStatus(FirewallRuleState.Created, ruleName, null);

        // netsh prints "Requested operation requires elevation" (exit 740) when
        // run without admin. Any non-zero exit means the rule wasn't created;
        // surface the message so the user knows why.
        return new FirewallRuleStatus(
            addResult.StdErr.Contains("elevation", StringComparison.OrdinalIgnoreCase) || addResult.ExitCode == 740
                ? FirewallRuleState.RequiresElevation
                : FirewallRuleState.Failed,
            ruleName,
            string.IsNullOrWhiteSpace(addResult.StdErr) ? addResult.StdOut : addResult.StdErr);
    }

    private static async Task<bool> RuleExistsAsync(string ruleName, CancellationToken ct)
    {
        var result = await RunNetshAsync(
            $"advfirewall firewall show rule name=\"{ruleName}\"", ct).ConfigureAwait(false);
        // netsh exit code is 0 when the rule exists, 1 when it doesn't.
        // Don't rely on the message text — that's localized.
        return result.ExitCode == 0;
    }

    private static async Task<NetshResult> RunNetshAsync(string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        try
        {
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to launch netsh.");
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return new NetshResult(proc.ExitCode, await stdoutTask, await stderrTask);
        }
        catch (Exception ex)
        {
            return new NetshResult(-1, string.Empty, ex.Message);
        }
    }

    private sealed record NetshResult(int ExitCode, string StdOut, string StdErr);
}

public enum FirewallRuleState
{
    AlreadyExists,
    Created,
    RequiresElevation,
    Failed,
}

public sealed record FirewallRuleStatus(FirewallRuleState State, string RuleName, string? Message);
