using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Deluno.Api.Network;

namespace Deluno.Tray;

/// <summary>
/// Runs one ticked component's own installer as its own process (#640).
///
/// <para>Nothing here merges a vendor's installer into Deluno's payload.
/// <see cref="InstallAsync"/> downloads the file the manifest names (or, for
/// Weir, resolves from its latest GitHub release), verifies it -- a pinned
/// SHA-256 where the manifest carries one, an Authenticode signature check
/// otherwise -- and only then spawns it. A component that fails either check
/// is refused, never run "just this once".</para>
///
/// <para>Detection (already installed / already answering its default port)
/// is <see cref="IInstalledProgramsReader"/> and <see cref="IPortProbe"/> from
/// Deluno.Api.Network -- the same code the running Deluno reads for the setup
/// wizard's "detected on this machine" prompt (#640's other half), so the
/// picker and the wizard can never disagree about what is already there.</para>
/// </summary>
public enum ComponentInstallOutcome
{
    AlreadyInstalled,
    Installed,
    DownloadFailed,
    VerificationFailed,
    InstallerFailed,
    StartFailed,
    ReadinessTimedOut
}

public sealed record ComponentInstallResult(string Kind, ComponentInstallOutcome Outcome, string Message);

public interface IComponentDownloader
{
    /// <summary>Downloads <paramref name="url"/> to a local file and returns its path, or null on failure.</summary>
    Task<string?> DownloadAsync(string url, string suggestedFileName, CancellationToken cancellationToken);
}

public interface IAuthenticodeVerifier
{
    /// <summary>Whether the file at <paramref name="path"/> carries a valid Authenticode signature.</summary>
    bool IsSigned(string path);
}

public interface IProcessRunner
{
    /// <summary>Runs <paramref name="path"/> with <paramref name="arguments"/> and returns its exit code, or null if it could not start.</summary>
    Task<int?> RunAsync(string path, string? arguments, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>
    /// The same, but asks Windows for administrator rights first (its own permission
    /// prompt, none when already elevated), with no window of its own. A per-machine
    /// .msi run unattended needs it: without elevation Windows Installer refuses a
    /// quiet install. Runs the plain way where a runner has no notion of elevation.
    /// </summary>
    Task<int?> RunElevatedAsync(string path, string? arguments, TimeSpan timeout, CancellationToken cancellationToken)
        => RunAsync(path, arguments, timeout, cancellationToken);
}

/// <summary>A process started without waiting for it to exit -- for a component whose post-install step is a long-running tray app, not an installer.</summary>
public interface IStartedProcess
{
    bool HasExited { get; }

    /// <summary>Valid only once <see cref="HasExited"/> is true.</summary>
    int ExitCode { get; }
}

public interface IProcessStarter
{
    /// <summary>Starts <paramref name="path"/> with <paramref name="arguments"/> and returns immediately without waiting for it to exit, or null if it could not start.</summary>
    IStartedProcess? Start(string path, string? arguments);

    /// <summary>The same, with no console window when <paramref name="hidden"/> is true (a console-mode server such as NZBGet's <c>-s</c> or the Transmission daemon).</summary>
    IStartedProcess? Start(string path, string? arguments, bool hidden)
        => Start(path, arguments);
}

/// <summary>Reads a text file a vendor's own installer shipped, to build a seeded config from it.</summary>
public interface ITextFileReader
{
    /// <summary>The file's text, or null when it does not exist or cannot be read.</summary>
    string? ReadAllText(string path);
}

public sealed class RealTextFileReader : ITextFileReader
{
    public string? ReadAllText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Polls a component's own readiness endpoint rather than waiting on its process tree, for a component (like Weir) that keeps running once it is ready.</summary>
public interface IReadinessChecker
{
    /// <summary>
    /// One check of <paramref name="url"/>. With a non-empty <paramref name="field"/>,
    /// true when the JSON body has that field equal to <paramref name="expectedValue"/>
    /// (Weir's own <c>/ready</c>). With an empty <paramref name="field"/>, true as soon
    /// as the URL answers with any HTTP response at all -- a download client's Web
    /// UI/API commonly answers a bare GET with 401/403/409 (auth required, or a CSRF
    /// token missing) rather than 200, and that response is itself the proof the
    /// server is up and listening, which is all "ready" needs to mean here.
    /// </summary>
    Task<bool> CheckAsync(string url, string field, string expectedValue, CancellationToken cancellationToken);
}

/// <summary>Writes a component's own config file ahead of its first start, e.g. to enable a Web UI/API and pick its port before Deluno ever has to guess or probe for it.</summary>
public interface IConfigFileWriter
{
    /// <summary>Writes <paramref name="content"/> to <paramref name="path"/>, creating its directory if needed. Never overwrites a file that already exists -- an existing config belongs to whatever put it there, including a previous run.</summary>
    Task<bool> WriteIfAbsentAsync(string path, string content, CancellationToken cancellationToken);
}

/// <summary>Whether a prerequisite (e.g. the VC++ runtime Transmission's MSI refuses to install without) is already on this machine.</summary>
public interface IPrerequisiteRegistryReader
{
    /// <summary>True when HKLM\<paramref name="subKeyPath"/>'s own <paramref name="valueName"/> reads as a non-zero DWORD.</summary>
    bool IsPresent(string subKeyPath, string valueName);
}

public sealed class ComponentInstallService(
    IInstalledProgramsReader? programs = null,
    IPortProbe? portProbe = null,
    IComponentDownloader? downloader = null,
    IAuthenticodeVerifier? signatures = null,
    IProcessRunner? processes = null,
    IProcessStarter? processStarter = null,
    IReadinessChecker? readiness = null,
    IConfigFileWriter? configWriter = null,
    IPrerequisiteRegistryReader? prerequisiteRegistry = null,
    ITextFileReader? templates = null,
    IBundledComponentCredentialStore? credentialStore = null,
    IStartedComponentsRegistry? startedComponents = null)
{
    private static readonly TimeSpan ReadinessPollInterval = TimeSpan.FromMilliseconds(200);
    private readonly ITextFileReader _templates = templates ?? new RealTextFileReader();
    private readonly Lazy<IBundledComponentCredentialStore> _credentials = new(
        () => credentialStore ?? BundledComponentCredentialStores.CreateForDataRoot(AppSettings.Load().DataRoot));
    private readonly IStartedComponentsRegistry _startedComponents = startedComponents ?? new FileStartedComponentsRegistry();

    private readonly IInstalledProgramsReader _programs = programs ?? new RegistryInstalledProgramsReader();
    private readonly IPortProbe _portProbe = portProbe ?? new LoopbackPortProbe();
    private readonly IComponentDownloader _downloader = downloader ?? new HttpComponentDownloader();
    private readonly IAuthenticodeVerifier _signatures = signatures ?? new WindowsAuthenticodeVerifier();
    private readonly IProcessRunner _processes = processes ?? new RealProcessRunner();
    private readonly IProcessStarter _processStarter = processStarter ?? new RealProcessStarter();
    private readonly IReadinessChecker _readiness = readiness ?? new HttpReadinessChecker();
    private readonly IConfigFileWriter _configWriter = configWriter ?? new RealConfigFileWriter();
    private readonly IPrerequisiteRegistryReader _prerequisiteRegistry = prerequisiteRegistry ?? new RegistryPrerequisiteReader();

    /// <summary>Whether <paramref name="spec"/> is already on this machine, by uninstall entry.</summary>
    public bool IsAlreadyInstalled(BundledComponentSpec spec)
        => _programs.ReadDisplayNames().Any(name => name.Contains(spec.UninstallDisplayNameHint, StringComparison.OrdinalIgnoreCase));

    public async Task<ComponentInstallResult> InstallAsync(BundledComponentSpec spec, CancellationToken cancellationToken)
    {
        if (IsAlreadyInstalled(spec))
        {
            return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.AlreadyInstalled,
                $"{spec.DisplayName} is already installed; Deluno left it alone.");
        }

        var url = spec.Url;
        var expectedSha256 = spec.Sha256;
        if (string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(spec.GithubRepo))
        {
            var resolved = await GithubLatestRelease.ResolveAsync(spec, cancellationToken);
            if (resolved is null)
            {
                return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.DownloadFailed,
                    $"Could not find {spec.DisplayName}'s latest release on GitHub.");
            }
            url = resolved.AssetUrl;
            expectedSha256 = resolved.Sha256;
        }
        else if (string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(spec.DirectoryListingUrl))
        {
            var resolved = await DirectoryListingLatestRelease.ResolveAsync(spec, cancellationToken);
            if (resolved is null)
            {
                return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.DownloadFailed,
                    $"Could not find {spec.DisplayName}'s latest Windows installer.");
            }
            url = resolved.AssetUrl;
            expectedSha256 = resolved.Sha256;
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.DownloadFailed,
                $"{spec.DisplayName} has no download source configured.");
        }

        var path = await _downloader.DownloadAsync(url, spec.FileName, cancellationToken);
        if (path is null)
        {
            return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.DownloadFailed,
                $"Could not download {spec.DisplayName}'s installer.");
        }

        var verification = VerifyWithSignature(spec, path, expectedSha256);
        if (verification is not null)
        {
            TryDelete(path);
            return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.VerificationFailed, verification);
        }

        if (spec.Prerequisite is not null)
        {
            var prerequisiteFailure = await EnsurePrerequisiteAsync(spec, cancellationToken);
            if (prerequisiteFailure is not null)
            {
                TryDelete(path);
                return prerequisiteFailure;
            }
        }

        int? exitCode;
        var scriptPath = default(string);
        if (IsMsi(path))
        {
            // A per-machine .msi needs administrator rights to install quietly.
            var logPath = path + ".log";
            if (!string.IsNullOrWhiteSpace(spec.DisableServiceAfterInstall))
            {
                scriptPath = path + ".install.cmd";
                File.WriteAllText(scriptPath, ServiceInstallScript.Build(path, spec.SilentArgs, logPath, spec.DisableServiceAfterInstall));
                exitCode = await _processes.RunElevatedAsync("cmd.exe", $"/c \"\"{scriptPath}\"\"", TimeSpan.FromMinutes(5), cancellationToken);
            }
            else
            {
                var (msiPath, msiArguments) = BuildInstallCommand(path, spec.SilentArgs);
                exitCode = await _processes.RunElevatedAsync(msiPath, msiArguments, TimeSpan.FromMinutes(5), cancellationToken);
            }
        }
        else
        {
            var (runPath, runArguments) = BuildInstallCommand(path, spec.SilentArgs);
            exitCode = await _processes.RunAsync(runPath, runArguments, TimeSpan.FromMinutes(5), cancellationToken);
        }

        TryDelete(path);
        if (scriptPath is not null)
        {
            TryDelete(scriptPath);
        }

        if (exitCode is null or not 0)
        {
            return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.InstallerFailed,
                $"{spec.DisplayName}'s installer did not finish cleanly (exit code {exitCode?.ToString() ?? "none"}).");
        }

        await SeedConfigAsync(spec, cancellationToken);

        if (string.IsNullOrWhiteSpace(spec.PostInstallCommand))
        {
            return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.Installed, $"{spec.DisplayName} installed.");
        }

        return await StartAndWaitForReadyAsync(spec, cancellationToken);
    }

    /// <summary>
    /// A per-machine .msi cannot reliably be double-clicked (ShellExecute's
    /// file-type association) in an unattended install: Windows Installer's
    /// own service needs the request to arrive through msiexec.exe itself,
    /// with an explicit install context, or it can fail with a bare exit code
    /// 1603 that names nothing wrong. Running msiexec.exe directly -- rather
    /// than the .msi path -- with ALLUSERS=1 (forces a per-machine install
    /// context instead of leaving it ambiguous) and a verbose log fixes the
    /// unattended case Transmission's installer hit on a clean VM.
    /// </summary>
    private static bool IsMsi(string installerPath) => installerPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Seeds the component's config ahead of its first start (only when no config
    /// exists yet). A component that needs sign-in credentials gets a generated
    /// password, written into the config in the vendor's own format and kept in
    /// Deluno's secret store -- and only when the config really was written, so a
    /// stored password never disagrees with a config the client is using (#1050).
    /// </summary>
    private async Task SeedConfigAsync(BundledComponentSpec spec, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(spec.ConfigFilePath))
        {
            return;
        }

        var credentials = string.IsNullOrWhiteSpace(spec.CredentialsUsername)
            ? null
            : new BundledComponentCredentials(spec.CredentialsUsername, GeneratedPassword.Create());

        string? content;
        if (!string.IsNullOrWhiteSpace(spec.ConfigTemplateSourcePath))
        {
            var template = _templates.ReadAllText(Environment.ExpandEnvironmentVariables(spec.ConfigTemplateSourcePath));
            content = template is null
                ? null
                : ComponentConfig.ApplyReplacements(template, spec.ConfigReplacements ?? new Dictionary<string, string>(), credentials);
        }
        else
        {
            content = string.IsNullOrWhiteSpace(spec.ConfigFileContent)
                ? null
                : ComponentConfig.RenderPlaceholders(spec.ConfigFileContent, credentials);
        }

        if (content is null)
        {
            return;
        }

        var configPath = Environment.ExpandEnvironmentVariables(spec.ConfigFilePath);
        var written = await _configWriter.WriteIfAbsentAsync(configPath, content, cancellationToken);
        if (written && credentials is not null)
        {
            try
            {
                _credentials.Value.Save(spec.Kind, credentials);
            }
            catch
            {
                // The client still works; the connection just has to be filled in by hand.
            }
        }
    }

    private static (string Path, string? Arguments) BuildInstallCommand(string installerPath, string? silentArgs)
    {
        if (!IsMsi(installerPath))
        {
            return (installerPath, silentArgs);
        }

        var logPath = installerPath + ".log";
        var arguments = $"/i \"{installerPath}\" ALLUSERS=1 {silentArgs} /l*v \"{logPath}\"";
        return ("msiexec.exe", arguments);
    }

    /// <summary>
    /// For a component whose installer only installs -- Weir's Setup runs and
    /// exits, it never starts Weir (#968) -- this starts the component's own
    /// executable and confirms it is ready by polling its readiness endpoint,
    /// rather than waiting on the process tree: the started process is a
    /// long-running tray app that is still running once it is ready, not an
    /// installer that exits when it finishes.
    /// </summary>
    private async Task<ComponentInstallResult> StartAndWaitForReadyAsync(BundledComponentSpec spec, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(spec.PreStartCommand))
        {
            // Fire-and-forget: a second, untracked process (Deluge's own
            // deluged, which deluge-web talks to) started ahead of the main
            // one. Its exit state is never polled -- only the main
            // PostInstallCommand process is tracked for readiness -- so a
            // failure here does not fail the whole install; it degrades to
            // "the Web UI came up but has no host configured yet", which is
            // still an improvement over not starting at all.
            _processStarter.Start(Environment.ExpandEnvironmentVariables(spec.PreStartCommand), ComponentAutoStarter.ExpandArgs(spec.PreStartArgs), spec.StartHidden);
        }

        var command = Environment.ExpandEnvironmentVariables(spec.PostInstallCommand!);
        var started = _processStarter.Start(command, ComponentAutoStarter.ExpandArgs(spec.PostInstallArgs), spec.StartHidden);
        if (started is null)
        {
            return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.StartFailed,
                $"{spec.DisplayName} installed, but Deluno could not start it.");
        }

        if (spec.RestartsAtSignIn)
        {
            _startedComponents.Add(spec.Kind);
        }

        if (string.IsNullOrWhiteSpace(spec.ReadinessUrl))
        {
            // No readiness endpoint configured: the best this can do is
            // confirm the process did not exit immediately.
            return started.HasExited
                ? new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.StartFailed,
                    $"{spec.DisplayName} installed, but exited immediately after starting (exit code {started.ExitCode}).")
                : new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.Installed, $"{spec.DisplayName} installed and started.");
        }

        var timeout = TimeSpan.FromSeconds(spec.ReadinessTimeoutSeconds ?? 60);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (started.HasExited)
            {
                return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.StartFailed,
                    $"{spec.DisplayName} exited before it finished starting (exit code {started.ExitCode}). Check its own log for why.");
            }

            if (await _readiness.CheckAsync(spec.ReadinessUrl, spec.ReadinessField ?? string.Empty, spec.ReadinessValue ?? string.Empty, cancellationToken))
            {
                return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.Installed, $"{spec.DisplayName} installed and is ready.");
            }

            if (DateTime.UtcNow >= deadline)
            {
                return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.ReadinessTimedOut,
                    $"{spec.DisplayName} started but did not report ready in time.");
            }

            await Task.Delay(ReadinessPollInterval, cancellationToken);
        }
    }

    /// <summary>
    /// Installs <see cref="BundledComponentSpec.Prerequisite"/> -- e.g. the
    /// VC++ runtime Transmission's own .msi LaunchConditions refuse without
    /// (exit code 1603) -- before the component's own installer runs.
    /// Skipped entirely when the registry already shows it present. Returns
    /// null to let the caller proceed with the component's own install, or a
    /// failure result to stop there, the same outcomes and verification path
    /// (minimum size, then a pinned checksum or a required signature) as any
    /// other bundled installer -- a prerequisite is never run unverified
    /// either.
    /// </summary>
    private async Task<ComponentInstallResult?> EnsurePrerequisiteAsync(BundledComponentSpec spec, CancellationToken cancellationToken)
    {
        var prerequisite = spec.Prerequisite!;
        if (_prerequisiteRegistry.IsPresent(prerequisite.SkipRegistryKeyPath, prerequisite.SkipRegistryValueName))
        {
            return null;
        }

        var path = await _downloader.DownloadAsync(prerequisite.Url, prerequisite.FileName, cancellationToken);
        if (path is null)
        {
            return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.DownloadFailed,
                $"Could not download {prerequisite.Name}, required before {spec.DisplayName} can install.");
        }

        var length = new FileInfo(path).Length;
        if (length < prerequisite.MinimumBytes)
        {
            TryDelete(path);
            return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.VerificationFailed,
                $"The downloaded file for {prerequisite.Name} is only {length} bytes -- far smaller than expected ({prerequisite.MinimumBytes}). Deluno did not run it.");
        }

        if (prerequisite.MustBeSigned && !_signatures.IsSigned(path))
        {
            TryDelete(path);
            return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.VerificationFailed,
                $"{prerequisite.Name} is not Authenticode-signed. Deluno did not run it.");
        }

        var exitCode = await _processes.RunAsync(path, prerequisite.SilentArgs, TimeSpan.FromMinutes(5), cancellationToken);
        TryDelete(path);

        // Microsoft's own redistributable installers (and Windows Installer
        // generally) report 3010 for "succeeded, reboot required" -- still a
        // success, not a reason to refuse the component that needs it.
        if (exitCode is null || (exitCode != 0 && exitCode != 3010))
        {
            return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.InstallerFailed,
                $"{prerequisite.Name} did not install cleanly (exit code {exitCode?.ToString() ?? "none"}), so {spec.DisplayName} was not installed.");
        }

        return null;
    }

    /// <summary>
    /// A pinned hash wins when the manifest carries one -- it is the stronger
    /// check and does not depend on the vendor having signed the file at all
    /// (qBittorrent's Windows build historically has not). Otherwise a
    /// required Authenticode signature is checked. A component with neither
    /// is refused rather than run unverified.
    /// </summary>
    private string? VerifyWithSignature(BundledComponentSpec spec, string path, string? expectedSha256)
    {
        var length = new FileInfo(path).Length;
        if (length < spec.MinimumBytes)
        {
            return $"The downloaded file for {spec.DisplayName} is only {length} bytes -- far smaller than expected ({spec.MinimumBytes}). Deluno did not run it.";
        }

        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            var actual = Sha256Of(path);
            return string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase)
                ? null
                : $"{spec.DisplayName}'s installer did not match its expected checksum. Deluno did not run it.";
        }

        if (spec.MustBeSigned)
        {
            return _signatures.IsSigned(path)
                ? null
                : $"{spec.DisplayName}'s installer is not Authenticode-signed and carries no pinned checksum. Deluno did not run it.";
        }

        return $"{spec.DisplayName}'s installer carries no pinned checksum and the manifest does not require a signature. Deluno did not run it.";
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort cleanup */ }
    }
}

/// <summary>Writes to the real filesystem, expanding nothing itself -- the caller already expanded the path.</summary>
public sealed class RealConfigFileWriter : IConfigFileWriter
{
    public Task<bool> WriteIfAbsentAsync(string path, string content, CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(path))
            {
                return Task.FromResult(false);
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, content);
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }
}

/// <summary>Reads a prerequisite's own install marker straight from the registry, no administrator rights needed since it only reads HKLM.</summary>
public sealed class RegistryPrerequisiteReader : IPrerequisiteRegistryReader
{
    public bool IsPresent(string subKeyPath, string valueName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return ReadOnWindows(subKeyPath, valueName);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool ReadOnWindows(string subKeyPath, string valueName)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(subKeyPath);
            return key?.GetValue(valueName) is int value && value != 0;
        }
        catch
        {
            // A locked-down registry hive or a malformed value degrades to
            // "not present", so the prerequisite is (re-)installed rather
            // than the component silently skipping a check it could not run.
            return false;
        }
    }
}

public sealed class HttpComponentDownloader : IComponentDownloader
{
    public async Task<string?> DownloadAsync(string url, string suggestedFileName, CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Deluno-Installer");
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var path = Path.Combine(Path.GetTempPath(), "Deluno-components", Guid.NewGuid().ToString("N")[..8] + "-" + suggestedFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var file = File.Create(path);
            await response.Content.CopyToAsync(file, cancellationToken);
            return path;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Reads the file's own Authenticode certificate, if it has one. This does
/// not walk the trust chain -- it answers "does this file carry a signature
/// at all", which is what the manifest's <c>mustBeSigned</c> flag asks for.
/// </summary>
public sealed class WindowsAuthenticodeVerifier : IAuthenticodeVerifier
{
    public bool IsSigned(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile predates the X509CertificateLoader API; no direct replacement exists for reading an embedded Authenticode signature.
            using var certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return certificate.Subject.Length > 0;
        }
        catch
        {
            return false;
        }
    }
}

public sealed class RealProcessRunner : IProcessRunner
{
    public Task<int?> RunAsync(string path, string? arguments, TimeSpan timeout, CancellationToken cancellationToken)
        => RunCoreAsync(new ProcessStartInfo(path, arguments ?? string.Empty) { UseShellExecute = true }, timeout, cancellationToken);

    /// <summary>Windows' own permission prompt (none when already elevated), then the process with no window: the person sees one prompt, not an installer.</summary>
    public Task<int?> RunElevatedAsync(string path, string? arguments, TimeSpan timeout, CancellationToken cancellationToken)
        => RunCoreAsync(
            new ProcessStartInfo(path, arguments ?? string.Empty) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden },
            timeout,
            cancellationToken);

    private static async Task<int?> RunCoreAsync(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(budget.Token);
                return process.ExitCode;
            }
            catch (OperationCanceledException)
            {
                // A timed-out or cancelled installer must not keep running
                // detached: without this, Deluno reports the component
                // failed while the vendor's own installer process (and
                // anything it already spawned) is left running unsupervised.
                TryKillTree(process);
                return null;
            }
        }
        catch
        {
            return null;
        }
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already exited, or Windows refused the kill; nothing more this can do.
        }
    }
}

/// <summary>
/// Starts a long-running component (Weir's own tray app) without waiting for
/// it to exit -- it is not an installer, so there is nothing to wait for.
/// Runs it directly (no shell) since it is a real executable path, not a
/// document needing a file-type association.
/// </summary>
public sealed class RealProcessStarter : IProcessStarter
{
    public IStartedProcess? Start(string path, string? arguments) => Start(path, arguments, hidden: false);

    public IStartedProcess? Start(string path, string? arguments, bool hidden)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo(path, arguments ?? string.Empty)
            {
                UseShellExecute = false,
                CreateNoWindow = hidden
            });
            return process is null ? null : new RealStartedProcess(process);
        }
        catch
        {
            return null;
        }
    }

    private sealed class RealStartedProcess(Process process) : IStartedProcess
    {
        public bool HasExited => process.HasExited;
        public int ExitCode => process.HasExited ? process.ExitCode : -1;
    }
}

/// <summary>Polls a readiness URL once per call; <see cref="ComponentInstallService"/> owns the retry loop and timeout.</summary>
public sealed class HttpReadinessChecker : IReadinessChecker
{
    public async Task<bool> CheckAsync(string url, string field, string expectedValue, CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await http.GetAsync(url, cancellationToken);

            if (string.IsNullOrWhiteSpace(field))
            {
                // No field to check: a download client's Web UI/API commonly
                // answers a bare unauthenticated GET with 401/403/409 rather
                // than 200 (Transmission's RPC wants its CSRF header first),
                // and that response is itself the proof the port is up and
                // its server is listening -- which is what "ready" means for
                // a component with no single JSON readiness body of its own.
                return true;
            }

            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty(field, out var value))
            {
                return false;
            }

            var actual = value.ValueKind switch
            {
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.String => value.GetString() ?? string.Empty,
                _ => value.GetRawText()
            };
            return string.Equals(actual, expectedValue, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
