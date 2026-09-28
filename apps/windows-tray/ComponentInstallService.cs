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
}

/// <summary>Polls a component's own readiness endpoint rather than waiting on its process tree, for a component (like Weir) that keeps running once it is ready.</summary>
public interface IReadinessChecker
{
    /// <summary>One check of <paramref name="url"/>: true when its JSON body has <paramref name="field"/> equal to <paramref name="expectedValue"/>.</summary>
    Task<bool> CheckAsync(string url, string field, string expectedValue, CancellationToken cancellationToken);
}

public sealed class ComponentInstallService(
    IInstalledProgramsReader? programs = null,
    IPortProbe? portProbe = null,
    IComponentDownloader? downloader = null,
    IAuthenticodeVerifier? signatures = null,
    IProcessRunner? processes = null,
    IProcessStarter? processStarter = null,
    IReadinessChecker? readiness = null)
{
    private static readonly TimeSpan ReadinessPollInterval = TimeSpan.FromMilliseconds(200);

    private readonly IInstalledProgramsReader _programs = programs ?? new RegistryInstalledProgramsReader();
    private readonly IPortProbe _portProbe = portProbe ?? new LoopbackPortProbe();
    private readonly IComponentDownloader _downloader = downloader ?? new HttpComponentDownloader();
    private readonly IAuthenticodeVerifier _signatures = signatures ?? new WindowsAuthenticodeVerifier();
    private readonly IProcessRunner _processes = processes ?? new RealProcessRunner();
    private readonly IProcessStarter _processStarter = processStarter ?? new RealProcessStarter();
    private readonly IReadinessChecker _readiness = readiness ?? new HttpReadinessChecker();

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

        var exitCode = await _processes.RunAsync(path, spec.SilentArgs, TimeSpan.FromMinutes(5), cancellationToken);
        TryDelete(path);

        if (exitCode is null or not 0)
        {
            return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.InstallerFailed,
                $"{spec.DisplayName}'s installer did not finish cleanly (exit code {exitCode?.ToString() ?? "none"}).");
        }

        if (string.IsNullOrWhiteSpace(spec.PostInstallCommand))
        {
            return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.Installed, $"{spec.DisplayName} installed.");
        }

        return await StartAndWaitForReadyAsync(spec, cancellationToken);
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
        var command = Environment.ExpandEnvironmentVariables(spec.PostInstallCommand!);
        var started = _processStarter.Start(command, spec.PostInstallArgs);
        if (started is null)
        {
            return new ComponentInstallResult(spec.Kind, ComponentInstallOutcome.StartFailed,
                $"{spec.DisplayName} installed, but Deluno could not start it.");
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
    public async Task<int?> RunAsync(string path, string? arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path, arguments ?? string.Empty)
            {
                UseShellExecute = true
            });
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
    public IStartedProcess? Start(string path, string? arguments)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo(path, arguments ?? string.Empty)
            {
                UseShellExecute = false
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
        if (string.IsNullOrWhiteSpace(field))
        {
            return false;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await http.GetAsync(url, cancellationToken);
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
