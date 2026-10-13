using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Deluno.Api.Network;
using Deluno.Companions;

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
    ReadinessTimedOut,
    /// <summary>Installed and running, but Deluno could not create its categories or set its folders (#1133); the result says what to do by hand.</summary>
    InstalledNeedsSetup
}

/// <param name="RetryAfterUtc">Set when it failed only because GitHub's limit on anonymous lookups was used up: when to try again.</param>
public sealed record ComponentInstallResult(string Kind, ComponentInstallOutcome Outcome, string Message, DateTimeOffset? RetryAfterUtc = null);

/// <summary>Where one component is in its install, for the progress window (#1140).</summary>
public enum ComponentStage
{
    Waiting,
    Downloading,
    Checking,
    Installing,
    Starting,
    SettingUp,
    Done,
    Failed
}

/// <param name="Detail">For a failed component, why; otherwise a short note on the stage.</param>
public sealed record ComponentProgress(string Kind, string DisplayName, ComponentStage Stage, string? Detail = null);

public interface IComponentDownloader
{
    /// <summary>Downloads <paramref name="url"/> to a local file and returns its path, or null on failure.</summary>
    Task<string?> DownloadAsync(string url, string suggestedFileName, CancellationToken cancellationToken);
}

/// <summary>Where the installer records what it checked for each component (#1061).</summary>
public interface IInstallLog
{
    void Write(string line);
}

/// <summary>The tray's install log, <c>%LocalAppData%\Deluno\logs\install-hook.log</c>, the file the install hooks already write to.</summary>
public sealed class FileInstallLog : IInstallLog
{
    public void Write(string line)
    {
        try
        {
            var root = Environment.GetEnvironmentVariable("DELUNO_TEST_LOCALAPPDATA") is { Length: > 0 } testRoot
                ? testRoot
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Deluno");
            var logDir = Path.Combine(root, "logs");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, "install-hook.log"), $"{DateTimeOffset.Now:O} {line}{Environment.NewLine}");
        }
        catch
        {
            // A log that cannot be written must never stop an install.
        }
    }
}

public interface IAuthenticodeVerifier
{
    /// <summary>Whether the file at <paramref name="path"/> carries a valid Authenticode signature.</summary>
    bool IsSigned(string path);

    /// <summary>
    /// Whether the file is signed with a certificate that chains to a trusted root and whose
    /// subject names <paramref name="publisher"/> (any signed file when no publisher is given).
    /// </summary>
    bool IsSignedBy(string path, string? publisher) => IsSigned(path);
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
    IStartedComponentsRegistry? startedComponents = null,
    IInstalledComponentRecord? installedRecord = null,
    TimeSpan? credentialCaptureWait = null,
    IInstallLog? installLog = null,
    Func<BundledComponentSpec, LocalWeirBuild?>? localBuild = null,
    IDownloadClientConfigurator? clientConfigurator = null)
{
    private readonly IDownloadClientConfigurator _clientConfigurator = clientConfigurator ?? new DownloadClientConfigurator(log: installLog ?? new FileInstallLog());
    private readonly IInstallLog _log = installLog ?? new FileInstallLog();
    // Test-only (#1158): a local Weir build the person driving a golden-path run named, installed instead of Weir's newest release.
    private readonly Func<BundledComponentSpec, LocalWeirBuild?> _localBuild = localBuild ?? (spec => LocalWeirBuild.Find(
        spec.Kind, name => Environment.GetEnvironmentVariable(name), AppContext.BaseDirectory));
    private static readonly TimeSpan ReadinessPollInterval = TimeSpan.FromMilliseconds(200);
    private readonly ITextFileReader _templates = templates ?? new RealTextFileReader();
    private readonly Lazy<IBundledComponentCredentialStore> _credentials = new(
        () => credentialStore ?? BundledComponentCredentialStores.CreateForDataRoot(AppSettings.RequireDataRoot()));
    private readonly IStartedComponentsRegistry _startedComponents = startedComponents ?? new FileStartedComponentsRegistry();
    private readonly Lazy<IInstalledComponentRecord> _installedRecord = new(
        () => installedRecord ?? new FileInstalledComponentRecord(FileInstalledComponentRecord.PathFor(
            Environment.GetEnvironmentVariable("DELUNO_TEST_LOCALAPPDATA") is { Length: > 0 } testRoot ? testRoot : AppSettings.RequireDataRoot())));
    private readonly TimeSpan _credentialCaptureWait = credentialCaptureWait ?? TimeSpan.FromSeconds(5);

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

    /// <summary>
    /// Installs one component and tells <paramref name="progress"/> each step it reaches (#1140), so a window can show
    /// downloading, checking, installing, started, or failed with the reason. A download client is then given its
    /// Movies and TV categories and the folders in <paramref name="layout"/> (#1133).
    /// </summary>
    public async Task<ComponentInstallResult> InstallAsync(BundledComponentSpec spec, CancellationToken cancellationToken, Action<ComponentProgress>? progress = null, DownloadLayout? layout = null, BundledComponentCredentials? chosenLogin = null)
    {
        ComponentInstallResult result;
        try
        {
            result = await InstallCoreAsync(spec, cancellationToken, progress ?? (_ => { }), layout, chosenLogin);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // One component failing must never stop the others (#1140).
            _log.Write($"{spec.DisplayName}: failed unexpectedly. {exception}");
            result = new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.InstallerFailed, $"{spec.DisplayName} could not be installed: {exception.Message}");
        }

        var failed = result.Outcome is ComponentInstallOutcome.DownloadFailed or ComponentInstallOutcome.VerificationFailed
            or ComponentInstallOutcome.InstallerFailed or ComponentInstallOutcome.StartFailed or ComponentInstallOutcome.ReadinessTimedOut;
        progress?.Invoke(new ComponentProgress(spec.Kind, spec.DisplayName, failed ? ComponentStage.Failed : ComponentStage.Done, result.Message));
        return result;
    }

    private async Task<ComponentInstallResult> InstallCoreAsync(BundledComponentSpec spec, CancellationToken cancellationToken, Action<ComponentProgress> progress, DownloadLayout? layout, BundledComponentCredentials? chosenLogin = null)
    {
        void Report(ComponentStage stage, string? detail = null) => progress(new ComponentProgress(spec.Kind, spec.DisplayName, stage, detail));
        if (IsAlreadyInstalled(spec))
        {
            return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.AlreadyInstalled,
                $"{spec.DisplayName} is already installed; Deluno left it alone.");
        }

        Report(ComponentStage.Downloading, "Finding the current version");
        var url = spec.Url;
        var expectedSha256 = spec.Sha256;
        string? path = null;
        var local = _localBuild(spec);
        if (local is not null)
        {
            // Test-only (#1158). The named file is copied, so the later clean-up never deletes the person's own
            // file, and it is checked against the SHA-256 they gave exactly as a download is.
            if (!File.Exists(local.Path))
            {
                return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.DownloadFailed,
                    $"The local {spec.DisplayName} build named for this test was not found.");
            }

            path = Path.Combine(Path.GetTempPath(), "Deluno-components", Guid.NewGuid().ToString("N")[..8] + "-" + spec.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(local.Path, path, overwrite: true);
            url = local.Path;
            expectedSha256 = local.Sha256;
            _log.Write($"{spec.DisplayName}: using local Weir build {local.Version()} {local.Sha256}");
        }
        else if (string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(spec.GithubRepo))
        {
            var lookup = await GithubLatestRelease.LookupAsync(spec, cancellationToken);
            var resolved = lookup.Asset;
            if (resolved is null)
            {
                if (lookup.RateLimited)
                {
                    // GitHub's limit, not a missing release: say so, and when it lifts (golden path, rc.30.8).
                    var retryAt = lookup.LimitResetsUtc ?? DateTimeOffset.UtcNow.AddHours(1);
                    _log.Write($"{spec.DisplayName}: GitHub's anonymous limit is used up until {retryAt:O}");
                    return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.DownloadFailed,
                        GithubLatestRelease.RateLimitMessage(retryAt), retryAt);
                }

                if (lookup.FeedNotFound)
                {
                    return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.DownloadFailed,
                        GithubLatestRelease.FeedNotFoundMessage(spec.DisplayName));
                }

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

        Report(ComponentStage.Downloading, "Downloading the installer");
        path ??= await _downloader.DownloadAsync(url, spec.FileName, cancellationToken);
        if (path is null)
        {
            return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.DownloadFailed,
                $"Could not download {spec.DisplayName}'s installer.");
        }

        Report(ComponentStage.Checking, "Checking the installer against its published checksum or signature");

        // The file is hashed before and after the checks, and that hash is what the run must
        // still match: a file swapped while it was being checked, or after, is refused (#1061).
        string verifiedSha256;
        var verification = default(string);
        try
        {
            var before = Sha256Of(path);
            verification = VerifyWithSignature(spec, path, expectedSha256);
            verifiedSha256 = Sha256Of(path);
            if (verification is null && !string.Equals(before, verifiedSha256, StringComparison.OrdinalIgnoreCase))
            {
                verification = $"{spec.DisplayName}'s installer changed while it was being checked. Deluno did not run it.";
            }
        }
        catch (IOException)
        {
            verifiedSha256 = string.Empty;
            verification = $"{spec.DisplayName}'s installer could not be read to check it. Deluno did not run it.";
        }

        if (verification is not null)
        {
            _log.Write($"{spec.DisplayName}: refused. {verification}");
            TryDelete(path);
            return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.VerificationFailed, verification);
        }

        _log.Write(local is not null
            ? $"{spec.DisplayName}: SHA-256 matches the checksum given with the local build"
            : $"{spec.DisplayName}: {DescribeVerification(spec, expectedSha256, checksumFromVendor: !string.IsNullOrWhiteSpace(expectedSha256) && string.IsNullOrWhiteSpace(spec.Sha256))}");

        // The installer is deleted once it has run, so this line is the only record of exactly which file was run (#1202).
        _log.Write($"{spec.DisplayName}: installer {Path.GetFileName(url ?? spec.FileName)} sha256 {verifiedSha256}");

        if (spec.Prerequisite is not null)
        {
            var prerequisiteFailure = await EnsurePrerequisiteAsync(spec, cancellationToken);
            if (prerequisiteFailure is not null)
            {
                TryDelete(path);
                return prerequisiteFailure;
            }
        }

        Report(ComponentStage.Installing, "Running its installer");
        int? exitCode;
        var lockedFolder = spec.RunsElevated || IsMsi(path);
        if (lockedFolder)
        {
            // An installer that runs with administrator rights (a per-machine .msi, or a vendor .exe that
            // asks for them) runs from a copy in a fresh folder only administrators can write, re-hashed
            // inside the elevated step; no script file is written, so nothing user-writable is run as
            // admin (#1061). With no pinned checksum the signature is the only gate, and it is checked
            // again on the copy that is actually run, inside the locked folder (#1061).
            var signatureIsTheGate = string.IsNullOrWhiteSpace(expectedSha256) && spec.MustBeSigned;
            var script = ElevatedInstallScript.Build(path, verifiedSha256, spec.SilentArgs, spec.DisableServiceAfterInstall,
                signatureIsTheGate ? spec.ExpectedPublisher : null, requireSignature: signatureIsTheGate, asExe: !IsMsi(path));
            exitCode = await _processes.RunElevatedAsync(ElevatedInstallScript.PowerShellPath, ElevatedInstallScript.PowerShellArguments(script), TimeSpan.FromMinutes(5), cancellationToken);
        }
        else
        {
            // An installer that installs for the signed-in person alone is not raised by Deluno, so
            // there is no elevated run to protect; the file still must be the one that was checked.
            if (!StillHasHash(path, verifiedSha256))
            {
                TryDelete(path);
                return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.VerificationFailed,
                    $"{spec.DisplayName}'s installer changed after it was checked. Deluno did not run it.");
            }

            var (runPath, runArguments) = BuildInstallCommand(path, spec.SilentArgs);
            exitCode = await _processes.RunAsync(runPath, runArguments, TimeSpan.FromMinutes(5), cancellationToken);
        }

        TryDelete(path);

        if (exitCode == ElevatedInstallScript.SignatureExitCode)
        {
            return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.VerificationFailed,
                $"{spec.DisplayName}'s installer was not validly signed by the expected publisher when Deluno was about to run it. Deluno did not run it.");
        }

        if (exitCode == ElevatedInstallScript.HashMismatchExitCode)
        {
            return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.VerificationFailed,
                $"{spec.DisplayName}'s installer changed after it was checked. Deluno did not run it.");
        }

        if (lockedFolder && exitCode is ElevatedInstallScript.ReparsePointExitCode or ElevatedInstallScript.FolderExitCode)
        {
            return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.VerificationFailed,
                $"Deluno could not make a safe folder to run {spec.DisplayName}'s installer from, so it did not run it.");
        }

        if (exitCode is null or not 0)
        {
            return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.InstallerFailed,
                $"{spec.DisplayName}'s installer did not finish cleanly (exit code {exitCode?.ToString() ?? "none"}).");
        }

        // From here the app is on this computer because the picker put it there, so the
        // running Deluno connects it (once), whether or not the start below succeeds (#1054).
        // Recorded before the config is seeded, so a sign-in kept for the app never exists
        // without the record that says the app is installed (#1061).
        RecordInstalled(spec);
        var loginProblem = await SeedConfigAsync(spec, chosenLogin, cancellationToken);

        if (string.IsNullOrWhiteSpace(spec.PostInstallCommand))
        {
            return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.Installed, $"{spec.DisplayName} installed.");
        }

        // Weir's supervised start can take a few minutes after an update; say so while the wait lasts (#1258).
        Report(ComponentStage.Starting, string.Equals(spec.Kind, "weir", StringComparison.OrdinalIgnoreCase) ? Deluno.Contracts.WeirStartWait.Notice : "Starting it and waiting until it answers");
        var started = await StartAndWaitForReadyAsync(spec, cancellationToken);
        await CaptureGeneratedCredentialsAsync(spec, chosenLogin, cancellationToken);
        if (loginProblem is not null)
        {
            _log.Write($"{spec.DisplayName}: the login chosen in the picker could not be written to its settings.");
            return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.InstalledNeedsSetup,
                $"{spec.DisplayName} installed and running, but {loginProblem}");
        }

        if (started.Outcome != ComponentInstallOutcome.Installed || layout is null
            || !string.Equals(spec.Category, BundledComponentSpec.DownloadClientCategory, StringComparison.Ordinal))
        {
            return started;
        }

        Report(ComponentStage.SettingUp, "Adding the Movies and TV categories and the download folders");
        string? setupProblem;
        try
        {
            setupProblem = await _clientConfigurator.ConfigureAsync(spec, layout, _credentials.Value.TryGet(spec.Kind), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            setupProblem = $"Deluno could not finish setting up {spec.DisplayName}: {exception.Message}";
        }

        if (setupProblem is null)
        {
            _log.Write($"{spec.DisplayName}: categories and download folders set up.");
            return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.Installed,
                $"{spec.DisplayName} installed and set up with Movies and TV categories and your download folders.");
        }

        _log.Write($"{spec.DisplayName}: {setupProblem}");
        return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.InstalledNeedsSetup,
            $"{spec.DisplayName} installed and running. {setupProblem} Add the Movies and TV categories and the download folders in {spec.DisplayName} itself.");
    }

    private void RecordInstalled(BundledComponentSpec spec)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                _installedRecord.Value.MarkInstalled(spec.Kind, "localhost", spec.DefaultPort);
                return;
            }
            catch when (attempt < 3)
            {
                Thread.Sleep(200);
            }
            catch (Exception exception)
            {
                // Still not recorded: the connection is made with "Use X", and a kept
                // sign-in is only removed for an app that is no longer installed. Say so (#1260).
                _log.Write($"{spec.DisplayName}: Deluno could not note the install in its list of installed apps. {exception.Message}");
            }
        }
    }

    /// <summary>
    /// SABnzbd makes its own API key the first time it runs and writes it to its own ini,
    /// which only the account that ran it can read. Read it here, in the install session,
    /// into the same secret store the other clients' sign-ins use, so the running Deluno can
    /// connect with it (#1054). The key is the "password"; there is no user name.
    /// </summary>
    private async Task CaptureGeneratedCredentialsAsync(BundledComponentSpec spec, BundledComponentCredentials? chosenLogin, CancellationToken cancellationToken)
    {
        if (!string.Equals(spec.Kind, "sabnzbd", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var deadline = DateTime.UtcNow + _credentialCaptureWait;
        while (true)
        {
            foreach (var path in new[] { @"%LocalAppData%\sabnzbd\sabnzbd.ini", @"%AppData%\SABnzbd\sabnzbd.ini" })
            {
                var key = SabnzbdIni.ApiKey(_templates.ReadAllText(Environment.ExpandEnvironmentVariables(path)));
                if (key is not null)
                {
                    try
                    {
                        // SABnzbd keeps its own sign-in defaults; the "password" Deluno's
                        // connection uses is its own API key, and there is no user name.
                        var seededUser = chosenLogin?.Username ?? _credentials.Value.TryGet(spec.Kind)?.Username ?? string.Empty;
                        _credentials.Value.Save(spec.Kind, new BundledComponentCredentials(seededUser, key));
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        // The connection just has to be filled in by hand; the reason is in the install log (#1260).
                        _log.Write($"{spec.DisplayName}: Deluno could not keep the sign-in it captured. {exception.Message}");
                    }

                    return;
                }
            }

            if (DateTime.UtcNow >= deadline)
            {
                // The seeded web sign-in is not an API key; the connection must not use it as one.
                try
                {
                    _credentials.Value.Remove(spec.Kind);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _log.Write($"{spec.DisplayName}: Deluno could not forget the sign-in it no longer needs. {exception.Message}");
                }

                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
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
    private async Task<string?> SeedConfigAsync(BundledComponentSpec spec, BundledComponentCredentials? chosenLogin, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(spec.ConfigFilePath))
        {
            return null;
        }

        // An older install may still have the component's folder in the install root: bring it over first, so nothing is lost (#1177).
        await ComponentStateMigration.RunAsync(ComponentPaths.InstallRoot(), ComponentPaths.DataRoot(), [], null, null, spec.DefaultPort, null, _log, cancellationToken);

        // SABnzbd and NZBGet get the login the person chose in the picker (#1142); qBittorrent a generated one.
        var credentials = string.IsNullOrWhiteSpace(spec.CredentialsUsername)
            ? null
            : new BundledComponentCredentials(spec.CredentialsUsername, GeneratedPassword.Create());

        string? content;
        if (!string.IsNullOrWhiteSpace(spec.ConfigTemplateSourcePath))
        {
            var template = _templates.ReadAllText(ComponentPaths.Expand(spec.ConfigTemplateSourcePath));
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

        string? loginProblem = null;
        if (chosenLogin is not null && UsenetLogins.IsUsenetKind(spec.Kind))
        {
            var withLogin = content is null ? null : UsenetLogins.WriteInto(spec.Kind, content, chosenLogin);
            if (withLogin is null)
            {
                // The vendor's file no longer looks the way this expects: leave it unwritten rather than guess.
                loginProblem = $"Deluno could not put the login you chose into its settings. It keeps the login it ships with; set your own in {spec.DisplayName} itself.";
                content = null;
            }
            else
            {
                content = withLogin;
                // SABnzbd's connection uses its API key, captured after it starts; NZBGet's uses this login.
                credentials = string.Equals(spec.Kind, "sabnzbd", StringComparison.OrdinalIgnoreCase) ? null : chosenLogin;
            }
        }

        if (content is null)
        {
            return loginProblem;
        }

        var configPath = ComponentPaths.Expand(spec.ConfigFilePath);
        var written = await _configWriter.WriteIfAbsentAsync(configPath, content, cancellationToken);
        if (written && credentials is not null)
        {
            try
            {
                _credentials.Value.Save(spec.Kind, credentials);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The client still works; the connection just has to be filled in by hand, and the reason is in the install log (#1260).
                _log.Write($"{spec.DisplayName}: Deluno could not keep the sign-in it generated. {exception.Message}");
            }
        }

        return null;
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
            _processStarter.Start(ComponentPaths.Expand(spec.PreStartCommand), ComponentAutoStarter.ExpandArgs(spec.PreStartArgs), spec.StartHidden);
        }

        var command = ComponentPaths.Expand(spec.PostInstallCommand!);
        var started = _processStarter.Start(command, ComponentAutoStarter.ExpandArgs(spec.PostInstallArgs), spec.StartHidden);
        if (started is null)
        {
            return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.StartFailed,
                $"{spec.DisplayName} installed, but Deluno could not start it.");
        }

        if (spec.RestartsAtSignIn)
        {
            try
            {
                _startedComponents.Add(spec.Kind);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _log.Write($"{spec.DisplayName}: Deluno could not note that it should start it at sign-in. {exception.Message}");
            }
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

        // A prerequisite has no pinned checksum, so its signature is the only gate, and that gate names
        // the publisher: a rule with no publisher would accept any signer Windows trusts.
        if (!prerequisite.MustBeSigned || string.IsNullOrWhiteSpace(prerequisite.ExpectedPublisher) || !_signatures.IsSignedBy(path, prerequisite.ExpectedPublisher))
        {
            TryDelete(path);
            return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.VerificationFailed,
                $"{prerequisite.Name} is not signed by its expected publisher. Deluno did not run it.");
        }

        _log.Write($"{prerequisite.Name}: signature valid, publisher {prerequisite.ExpectedPublisher}");

        // The same locked-folder run as the component itself: copied into a fresh administrators-only
        // folder, re-hashed and its signature checked again on that copy, then run from there.
        string checkedSha256;
        try
        {
            checkedSha256 = Sha256Of(path);
        }
        catch (IOException)
        {
            TryDelete(path);
            return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.VerificationFailed,
                $"{prerequisite.Name} could not be read to check it. Deluno did not run it.");
        }

        var script = ElevatedInstallScript.Build(path, checkedSha256, prerequisite.SilentArgs, null, prerequisite.ExpectedPublisher,
            requireSignature: true, asExe: true, alsoSuccessCodes: [3010]);
        var exitCode = await _processes.RunElevatedAsync(ElevatedInstallScript.PowerShellPath, ElevatedInstallScript.PowerShellArguments(script), TimeSpan.FromMinutes(5), cancellationToken);
        TryDelete(path);

        if (exitCode is ElevatedInstallScript.HashMismatchExitCode or ElevatedInstallScript.SignatureExitCode)
        {
            return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.VerificationFailed,
                $"{prerequisite.Name} changed after it was checked. Deluno did not run it.");
        }

        if (exitCode is ElevatedInstallScript.ReparsePointExitCode or ElevatedInstallScript.FolderExitCode)
        {
            return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.VerificationFailed,
                $"Deluno could not make a safe folder to run {prerequisite.Name}'s installer from, so it did not run it.");
        }

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
            if (string.IsNullOrWhiteSpace(spec.ExpectedPublisher))
            {
                // A signature rule with no named publisher would accept any signer Windows trusts.
                return $"{spec.DisplayName}'s installer must be signed but the manifest names no publisher and no checksum. Deluno did not run it.";
            }

            return _signatures.IsSignedBy(path, spec.ExpectedPublisher)
                ? null
                : $"{spec.DisplayName}'s installer is not Authenticode-signed and carries no pinned checksum. Deluno did not run it.";
        }

        return $"{spec.DisplayName}'s installer carries no pinned checksum and the manifest does not require a signature. Deluno did not run it.";
    }

    /// <summary>What the check that just passed was, in the words the install log records.</summary>
    private static string DescribeVerification(BundledComponentSpec spec, string? expectedSha256, bool checksumFromVendor)
    {
        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            return checksumFromVendor
                ? "SHA-256 matches the vendor's published checksum"
                : "SHA-256 matches the checksum pinned in Deluno's list of components";
        }

        return $"signature valid, publisher {spec.ExpectedPublisher}";
    }

    private static bool StillHasHash(string path, string sha256)
    {
        try
        {
            return string.Equals(Sha256Of(path), sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
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
            using var http = new HttpClient(Deluno.Infrastructure.Http.LoopbackFirstConnection.CreateHandler()) { Timeout = TimeSpan.FromMinutes(10) };
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
/// Authenticode check (#1061). <c>WinVerifyTrust</c> (the same engine
/// <c>Get-AuthenticodeSignature</c> uses) proves the signature covers the file's
/// bytes and chains to a trusted root, with revocation checked over the whole chain
/// as far as the machine can reach the servers. Only then is the signer's
/// certificate read, and its publisher compared exactly. A certificate lifted out of
/// a file whose bytes were changed is therefore refused.
/// </summary>
public sealed class WindowsAuthenticodeVerifier : IAuthenticodeVerifier
{
    public bool IsSignedBy(string path, string? publisher)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return false;
        }

        try
        {
            // One handle, opened so nothing can write to the file, is what is verified AND
            // where the publisher is read from: the two cannot be looking at different bytes.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!WinTrust.Verify(path, stream.SafeFileHandle.DangerousGetHandle()))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(publisher))
            {
                return true;
            }

#pragma warning disable SYSLIB0057 // CreateFromSignedFile predates the X509CertificateLoader API; no direct replacement exists for reading an embedded Authenticode signature.
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return PublisherMatches(certificate, publisher);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when the certificate's subject has a CN or O that is exactly <paramref name="publisher"/> (not a substring of it).</summary>
    public static bool PublisherMatches(X509Certificate2 certificate, string publisher)
    {
        var wanted = publisher.Trim();
        foreach (var rdn in certificate.SubjectName.EnumerateRelativeDistinguishedNames())
        {
            var type = rdn.GetSingleElementType().Value;
            if (type is not ("2.5.4.3" or "2.5.4.10"))
            {
                continue;
            }

            if (string.Equals(rdn.GetSingleElementValue()?.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the file carries a signature that is valid for its current bytes and chains to a trusted root.</summary>
    public bool IsSigned(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return false;
        }

        try
        {
            return WinTrust.Verify(path);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Managed wrapper for <c>WinVerifyTrust</c> with <c>WINTRUST_ACTION_GENERIC_VERIFY_V2</c>.</summary>
internal static class WinTrust
{
    private const uint UiNone = 2;
    private const uint RevokeWholeChain = 1;
    private const uint ChoiceFile = 1;
    private const uint StateActionIgnore = 0;
    private const uint RevocationCheckChainExcludeRoot = 0x80;
    // Revocation server unreachable: the signature is still good, so this is not a bad file.
    private const int CertERevocationOffline = unchecked((int)0x80092013);
    private const int CryptENoRevocationCheck = unchecked((int)0x80092012);

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, IntPtr data);

    public static bool Verify(string path, IntPtr fileHandle = default)
    {
        var filePathPtr = Marshal.StringToHGlobalUni(path);
        var fileInfoPtr = IntPtr.Zero;
        var dataPtr = IntPtr.Zero;
        try
        {
            var fileInfo = new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(), pcwszFilePath = filePathPtr, hFile = fileHandle };
            fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = UiNone,
                fdwRevocationChecks = RevokeWholeChain,
                dwUnionChoice = ChoiceFile,
                pFile = fileInfoPtr,
                dwStateAction = StateActionIgnore,
                dwProvFlags = RevocationCheckChainExcludeRoot
            };
            dataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(data, dataPtr, false);
            var result = WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, dataPtr);
            return result is 0 or CertERevocationOffline or CryptENoRevocationCheck;
        }
        finally
        {
            if (dataPtr != IntPtr.Zero) { Marshal.FreeHGlobal(dataPtr); }
            if (fileInfoPtr != IntPtr.Zero) { Marshal.FreeHGlobal(fileInfoPtr); }
            Marshal.FreeHGlobal(filePathPtr);
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
            using var http = new HttpClient(Deluno.Infrastructure.Http.LoopbackFirstConnection.CreateHandler()) { Timeout = TimeSpan.FromSeconds(5) };
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
