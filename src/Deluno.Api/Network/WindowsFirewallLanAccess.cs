using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace Deluno.Api.Network;

/// <summary>
/// Whether Windows Firewall lets other devices reach Deluno's port, and
/// Deluno's own rule for when it does not (#623).
///
/// <para>The question is whether the port is open, not whether Deluno's rule
/// exists. The rig had an older rule, "Deluno E2E lab - Deluno Host", for the
/// same port. Looking only for "Deluno (port 5099)" said other devices were
/// turned away while one was using Deluno. So this reads every inbound rule and
/// the firewall's own on/off state, the way Windows decides.</para>
///
/// <para>Deluno's rule covers every network profile. Windows files a home
/// network it has not been told about as Public, and the rig's LAN is exactly
/// that, so a rule limited to Private networks would change nothing.</para>
///
/// <para>This is the one place that reads or writes the rule. The installer,
/// the tray and the System screen all go through it.</para>
/// </summary>
public sealed class WindowsFirewallLanAccess(int port, IWindowsFirewall? firewall = null, string? programPath = null) : ILanAccess
{
    private readonly IWindowsFirewall _firewall = firewall ?? new WindowsFirewall();
    private readonly string? _programPath = programPath ?? Environment.ProcessPath;

    public int Port => port;

    public string RuleName => RuleNameFor(port);

    public static string RuleNameFor(int port) => $"Deluno (port {port})";

    public Task<LanAccessStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var snapshot = _firewall.Read();
        if (snapshot is null)
        {
            // Windows would not say; showing a warning on a guess would be worse.
            return Task.FromResult(new LanAccessStatus(
                LanAccessStates.NotChecked,
                port,
                RuleName,
                "Deluno could not read Windows Firewall's rules.",
                CanAllow: false));
        }

        return Task.FromResult(FirewallRules.LetsIn(snapshot, port, _programPath) is { } openedBy
            ? new LanAccessStatus(
                LanAccessStates.Open,
                port,
                openedBy,
                $"Windows Firewall lets other devices on your network reach Deluno on port {port}.",
                CanAllow: false)
            : Blocked(null));
    }

    public async Task<LanAccessStatus> AllowAsync(CancellationToken cancellationToken)
    {
        var current = await GetStatusAsync(cancellationToken);
        if (current.State == LanAccessStates.Open)
        {
            return current;
        }

        var add = await _firewall.RunNetshAsync(
            $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={port} profile=any",
            elevate: !_firewall.IsElevated,
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

    /// <summary>Removes Deluno's own rule. Run by the uninstaller, which leaves nothing behind.</summary>
    public Task<FirewallCommandResult> RemoveAsync(CancellationToken cancellationToken)
        => _firewall.RunNetshAsync($"advfirewall firewall delete rule name=\"{RuleName}\"", elevate: !_firewall.IsElevated, cancellationToken);

    /// <summary>Whether Deluno's own rule is there, for the uninstaller to remove.</summary>
    public bool HasOwnRule()
        => _firewall.Read()?.Rules.Any(rule => string.Equals(rule.Name, RuleName, StringComparison.OrdinalIgnoreCase)) == true;

    private LanAccessStatus Blocked(string? why) => new(
        LanAccessStates.Blocked,
        port,
        RuleName,
        why ?? $"Windows Firewall has no rule that lets other devices reach Deluno's port {port}, so they cannot open Deluno.",
        CanAllow: true);
}

/// <summary>What Windows Firewall holds, as far as an inbound connection is concerned.</summary>
/// <param name="CurrentProfiles">The profiles of the networks connected now (1 domain, 2 private, 4 public).</param>
/// <param name="FirewallOffFor">The current profiles whose firewall is switched off.</param>
public sealed record FirewallSnapshot(int CurrentProfiles, int FirewallOffFor, IReadOnlyList<FirewallRule> Rules);

/// <param name="Protocol">6 is TCP, 256 is any.</param>
/// <param name="LocalPorts">"*", a port, a range, or a comma-separated list of either; null means any.</param>
/// <param name="Program">The program the rule is limited to, or null for any.</param>
/// <param name="Service">The Windows service the rule is limited to, "*"/null for any.</param>
/// <param name="LocalAppPackageId">
/// Non-empty when the rule is scoped to an AppX/MSIX package rather than an
/// ordinary Win32 program (INetFwRule3, read only when the COM object
/// supports that interface). Deluno ships as a plain Win32 executable, never
/// as a package, so a rule scoped this way can never actually be Deluno's --
/// see <see cref="FirewallRules.LetsIn"/>.
/// </param>
public sealed record FirewallRule(
    string Name,
    bool Enabled,
    bool Inbound,
    bool Allow,
    int Protocol,
    string? LocalPorts,
    int Profiles,
    string? Program,
    string? Service = null,
    string? LocalAppPackageId = null);

public static class FirewallRules
{
    private const int Tcp = 6;
    private const int AnyProtocol = 256;

    /// <summary>
    /// What lets an inbound TCP connection to <paramref name="port"/> through,
    /// or null when nothing does: a firewall switched off for the network
    /// Deluno is on, or an enabled inbound allow rule for that port, that
    /// program and that network.
    ///
    /// <para>A rule scoped to an AppX/MSIX package (<see
    /// cref="FirewallRule.LocalAppPackageId"/> non-empty) or to a specific
    /// Windows service never counts, even when it otherwise looks like a
    /// wildcard "any port, any program" allow rule. Windows ships several of
    /// these by default -- "Desktop App Web Viewer", "Windows Feature
    /// Experience Pack", the OOBE package -- with <c>ApplicationName</c> and
    /// <c>LocalPorts</c> both empty, which previously read as "any program,
    /// any port" and reported Deluno reachable from the network when it
    /// verifiably was not (confirmed live: a real inbound connection timed
    /// out while GetStatusAsync reported "open" against one of these). Those
    /// rules only ever admit traffic for their own package's isolated
    /// network context, never for an ordinary Win32 process like Deluno.</para>
    /// </summary>
    public static string? LetsIn(FirewallSnapshot snapshot, int port, string? programPath)
    {
        if ((snapshot.FirewallOffFor & snapshot.CurrentProfiles) != 0)
        {
            return "Windows Firewall is off for this network";
        }

        return snapshot.Rules.FirstOrDefault(rule =>
            rule.Enabled &&
            rule.Inbound &&
            rule.Allow &&
            rule.Protocol is Tcp or AnyProtocol &&
            (rule.Profiles & snapshot.CurrentProfiles) != 0 &&
            CoversPort(rule.LocalPorts, port) &&
            CoversProgram(rule.Program, programPath) &&
            CoversService(rule.Service) &&
            !IsPackageScoped(rule))?.Name;
    }

    /// <summary>
    /// <see cref="FirewallRule.LocalAppPackageId"/> is the documented signal
    /// for a package-scoped rule (INetFwRule3), but it reads empty on the
    /// exact rules this exists to exclude -- confirmed by querying
    /// HNetCfg.FwPolicy2 directly for "Desktop App Web Viewer" and the OOBE
    /// package's rules on a real machine. Those are Windows' older,
    /// auto-generated AppX capability rules: their restriction to that
    /// package is enforced by Windows Filtering Platform against the calling
    /// process's AppContainer token, a check with no property on the plain
    /// rule object at all. What every one of them does carry is a <see
    /// cref="FirewallRule.Name"/> in Windows' indirect resource-string
    /// format -- <c>@{PackageFamilyName?ms-resource://...}</c> -- which a
    /// hand-created rule (netsh, PowerShell, an installer, Deluno's own)
    /// never produces. That format is the reliable signal here; a populated
    /// LocalAppPackageId is kept as a second, belt-and-braces check for
    /// whatever rule shape does carry it.
    /// </summary>
    private static bool IsPackageScoped(FirewallRule rule)
        => !string.IsNullOrWhiteSpace(rule.LocalAppPackageId)
           || LooksLikePackageResourceName(rule.Name);

    private static bool LooksLikePackageResourceName(string name)
        => name.StartsWith("@{", StringComparison.Ordinal) && name.Contains("ms-resource:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Deluno never runs as a named Windows service for the purposes of this
    /// check (it is reached as an ordinary program on its port), so a rule
    /// restricted to some OTHER service never covers it. Windows' own
    /// wildcard for "no service restriction" is "*", alongside the more
    /// common null/empty.
    /// </summary>
    private static bool CoversService(string? service)
        => string.IsNullOrWhiteSpace(service) || service.Trim() == "*";

    internal static bool CoversPort(string? localPorts, int port)
    {
        if (string.IsNullOrWhiteSpace(localPorts) || localPorts.Trim() == "*")
        {
            return true;
        }

        foreach (var part in localPorts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var range = part.Split('-', 2, StringSplitOptions.TrimEntries);
            if (range.Length == 2 && int.TryParse(range[0], out var low) && int.TryParse(range[1], out var high))
            {
                if (port >= low && port <= high)
                {
                    return true;
                }
            }
            else if (int.TryParse(part, out var single) && single == port)
            {
                return true;
            }
        }

        return false;
    }

    private static bool CoversProgram(string? ruleProgram, string? programPath)
        => string.IsNullOrWhiteSpace(ruleProgram) ||
           (!string.IsNullOrWhiteSpace(programPath) &&
            string.Equals(
                Environment.ExpandEnvironmentVariables(ruleProgram.Trim()),
                programPath,
                StringComparison.OrdinalIgnoreCase));
}

public sealed record FirewallCommandResult(int ExitCode, string Output, bool Declined = false);

public interface IWindowsFirewall
{
    /// <summary>Whether this process can change the firewall without asking.</summary>
    bool IsElevated { get; }

    /// <summary>The firewall's state and rules, or null when Windows will not say.</summary>
    FirewallSnapshot? Read();

    Task<FirewallCommandResult> RunNetshAsync(string netshArguments, bool elevate, CancellationToken cancellationToken);
}

/// <summary>
/// Reads Windows Firewall through its own API, HNetCfg.FwPolicy2, which needs
/// no administrator rights and answers the same in every language. Changes go
/// through netsh, asking Windows for permission first when told to elevate.
/// </summary>
public sealed class WindowsFirewall : IWindowsFirewall
{
    // ERROR_CANCELLED: the person said no to the permission prompt.
    private const int PermissionDeclined = 1223;
    private const int InboundDirection = 1;
    private const int AllowAction = 1;

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

    public FirewallSnapshot? Read()
    {
        if (!OperatingSystem.IsWindows() || Type.GetTypeFromProgID("HNetCfg.FwPolicy2") is not { } policyType)
        {
            return null;
        }

        try
        {
            dynamic policy = Activator.CreateInstance(policyType)!;
            int current = policy.CurrentProfileTypes;
            var off = 0;
            foreach (var profile in new[] { 1, 2, 4 })
            {
                if ((current & profile) != 0 && !(bool)policy.FirewallEnabled[profile])
                {
                    off |= profile;
                }
            }

            var rules = new List<FirewallRule>();
            foreach (dynamic rule in policy.Rules)
            {
                rules.Add(new FirewallRule(
                    (string)(rule.Name ?? string.Empty),
                    (bool)rule.Enabled,
                    (int)rule.Direction == InboundDirection,
                    (int)rule.Action == AllowAction,
                    (int)rule.Protocol,
                    (string?)rule.LocalPorts,
                    (int)rule.Profiles,
                    (string?)rule.ApplicationName,
                    TryReadString(() => rule.ServiceName),
                    TryReadString(() => rule.LocalAppPackageId)));
            }

            return new FirewallSnapshot(current, off, rules);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>LocalAppPackageId</c> is only on INetFwRule3; a rule object that
    /// does not implement it (an older Windows Firewall rule shape) throws
    /// on the property access rather than returning null, either as a COM
    /// interface-not-supported error or, through the dynamic dispatch here,
    /// a RuntimeBinderException. Either way, "this rule has no package
    /// restriction" is the right read, not "the whole rule is unreadable".
    /// </summary>
    private static string? TryReadString(Func<dynamic> read)
    {
        try
        {
            return (string?)read();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return null;
        }
    }

    private static readonly TimeSpan NetshLimit = TimeSpan.FromMinutes(2);

    public async Task<FirewallCommandResult> RunNetshAsync(string netshArguments, bool elevate, CancellationToken cancellationToken)
    {
        try
        {
            if (elevate)
            {
                // A permission prompt cannot be combined with reading the
                // output, so the caller reads the rules afterwards instead.
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

                // A shell start with no pipes, but not an open-ended wait (#1258).
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                limit.CancelAfter(NetshLimit);
                try
                {
                    await elevated.WaitForExitAsync(limit.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return new FirewallCommandResult(-1, "Windows did not finish the firewall change in time.");
                }

                return new FirewallCommandResult(elevated.ExitCode, string.Empty);
            }

            // Both streams are read as they arrive and netsh is stopped if it does not finish (#1258).
            var result = await Deluno.Infrastructure.Processes.ChildProcess.RunAsync(new ProcessStartInfo("netsh", netshArguments), NetshLimit, cancellationToken);
            if (!result.Started)
            {
                return new FirewallCommandResult(-1, "Windows did not start netsh.");
            }

            if (result.TimedOut)
            {
                return new FirewallCommandResult(-1, "netsh did not finish in time.");
            }

            return new FirewallCommandResult(result.ExitCode ?? -1, result.Output + result.Error);
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
