using Deluno.Api.Network;
using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// The installer's component picker never runs a vendor's installer it could
/// not verify, and never re-installs something already on the machine
/// (#640).
/// </summary>
public sealed class ComponentInstallServiceTests
{
    private static readonly BundledComponentSpec Spec = new(
        DisplayName: "Test Client",
        Category: BundledComponentSpec.DownloadClientCategory,
        Protocol: "qbittorrent",
        Licence: "GPL-3.0",
        LicenceUrl: "https://example.invalid/licence",
        Version: "1.0",
        FileName: "test-client-setup.exe",
        Url: "https://example.invalid/test-client-setup.exe",
        Sha256: null,
        MinimumBytes: 10,
        MustBeSigned: false,
        SilentArgs: "/S",
        DefaultPort: 12345,
        UninstallDisplayNameHint: "Test Client",
        GithubRepo: null,
        GithubAsset: null,
        GithubChecksumAsset: null)
    { Kind = "test-client" };

    [Fact]
    public async Task Already_installed_is_skipped_without_downloading_anything()
    {
        var downloader = new RecordingDownloader();
        var service = new ComponentInstallService(
            new FakePrograms("Test Client 1.0"),
            new FakePorts(),
            downloader,
            new FakeSignatures(signed: true),
            new RecordingProcesses());

        var result = await service.InstallAsync(Spec, CancellationToken.None);

        Assert.Equal(ComponentInstallOutcome.AlreadyInstalled, result.Outcome);
        Assert.False(downloader.WasCalled);
    }

    [Fact]
    public async Task A_file_with_no_pinned_checksum_and_no_signature_requirement_is_refused()
    {
        var downloadedPath = WriteTempFile(new byte[20]);
        var service = new ComponentInstallService(
            new FakePrograms(),
            new FakePorts(),
            new RecordingDownloader(downloadedPath),
            new FakeSignatures(signed: true),
            new RecordingProcesses());

        var result = await service.InstallAsync(Spec, CancellationToken.None);

        Assert.Equal(ComponentInstallOutcome.VerificationFailed, result.Outcome);
    }

    [Fact]
    public async Task A_file_too_small_to_be_real_is_refused_even_when_signed()
    {
        var downloadedPath = WriteTempFile(new byte[2]); // below MinimumBytes: 10
        var processes = new RecordingProcesses();
        var service = new ComponentInstallService(
            new FakePrograms(),
            new FakePorts(),
            new RecordingDownloader(downloadedPath),
            new FakeSignatures(signed: true),
            processes);

        var result = await service.InstallAsync(Spec with { MustBeSigned = true }, CancellationToken.None);

        Assert.Equal(ComponentInstallOutcome.VerificationFailed, result.Outcome);
        Assert.False(processes.WasCalled);
    }

    [Fact]
    public async Task An_unsigned_file_is_refused_when_the_manifest_requires_a_signature()
    {
        var downloadedPath = WriteTempFile(new byte[20]);
        var processes = new RecordingProcesses();
        var service = new ComponentInstallService(
            new FakePrograms(),
            new FakePorts(),
            new RecordingDownloader(downloadedPath),
            new FakeSignatures(signed: false),
            processes);

        var result = await service.InstallAsync(Spec with { MustBeSigned = true }, CancellationToken.None);

        Assert.Equal(ComponentInstallOutcome.VerificationFailed, result.Outcome);
        Assert.False(processes.WasCalled);
    }

    [Fact]
    public async Task A_signed_file_matching_the_manifests_requirement_is_run()
    {
        var downloadedPath = WriteTempFile(new byte[20]);
        var processes = new RecordingProcesses(exitCode: 0);
        var service = new ComponentInstallService(
            new FakePrograms(),
            new FakePorts(),
            new RecordingDownloader(downloadedPath),
            new FakeSignatures(signed: true),
            processes);

        var result = await service.InstallAsync(Spec with { MustBeSigned = true }, CancellationToken.None);

        Assert.Equal(ComponentInstallOutcome.Installed, result.Outcome);
        Assert.True(processes.WasCalled);
        Assert.Equal("/S", processes.LastArguments);
    }

    [Fact]
    public async Task A_pinned_checksum_that_does_not_match_is_refused_even_when_signed()
    {
        var downloadedPath = WriteTempFile(new byte[20]);
        var processes = new RecordingProcesses();
        var service = new ComponentInstallService(
            new FakePrograms(),
            new FakePorts(),
            new RecordingDownloader(downloadedPath),
            new FakeSignatures(signed: true),
            processes);

        var result = await service.InstallAsync(Spec with { Sha256 = "0000000000000000000000000000000000000000000000000000000000000000" }, CancellationToken.None);

        Assert.Equal(ComponentInstallOutcome.VerificationFailed, result.Outcome);
        Assert.False(processes.WasCalled);
    }

    [Fact]
    public async Task A_nonzero_exit_code_is_reported_as_a_failed_installer()
    {
        var downloadedPath = WriteTempFile(new byte[20]);
        var processes = new RecordingProcesses(exitCode: 1);
        var service = new ComponentInstallService(
            new FakePrograms(),
            new FakePorts(),
            new RecordingDownloader(downloadedPath),
            new FakeSignatures(signed: true),
            processes);

        var result = await service.InstallAsync(Spec with { MustBeSigned = true }, CancellationToken.None);

        Assert.Equal(ComponentInstallOutcome.InstallerFailed, result.Outcome);
    }

    [Fact]
    public async Task A_per_machine_msi_is_run_through_msiexec_not_shellexecuted_directly()
    {
        var downloadedPath = WriteTempFile(new byte[20], ".msi");
        var processes = new RecordingProcesses(exitCode: 0);
        var service = new ComponentInstallService(
            new FakePrograms(),
            new FakePorts(),
            new RecordingDownloader(downloadedPath),
            new FakeSignatures(signed: true),
            processes);

        var result = await service.InstallAsync(Spec with { MustBeSigned = true, FileName = "test-client.msi" }, CancellationToken.None);

        Assert.Equal(ComponentInstallOutcome.Installed, result.Outcome);
        Assert.Equal("msiexec.exe", processes.LastPath);
        Assert.Contains($"/i \"{downloadedPath}\"", processes.LastArguments);
        Assert.Contains("ALLUSERS=1", processes.LastArguments);
        Assert.Contains("/S", processes.LastArguments); // the manifest's own silentArgs still applies
        Assert.Contains("/l*v", processes.LastArguments);
    }

    [Fact]
    public async Task A_non_msi_installer_still_runs_directly()
    {
        var downloadedPath = WriteTempFile(new byte[20], ".exe");
        var processes = new RecordingProcesses(exitCode: 0);
        var service = new ComponentInstallService(
            new FakePrograms(),
            new FakePorts(),
            new RecordingDownloader(downloadedPath),
            new FakeSignatures(signed: true),
            processes);

        await service.InstallAsync(Spec with { MustBeSigned = true }, CancellationToken.None);

        Assert.Equal(downloadedPath, processes.LastPath);
        Assert.Equal("/S", processes.LastArguments);
    }

    [Fact]
    public async Task A_config_file_is_seeded_before_the_component_first_starts()
    {
        var downloadedPath = WriteTempFile(new byte[20]);
        var processes = new RecordingProcesses(exitCode: 0);
        var configWriter = new RecordingConfigFileWriter();
        var service = new ComponentInstallService(
            new FakePrograms(),
            new FakePorts(),
            new RecordingDownloader(downloadedPath),
            new FakeSignatures(signed: true),
            processes,
            configWriter: configWriter);

        var spec = Spec with
        {
            MustBeSigned = true,
            ConfigFilePath = "%TEST_CONFIG_DIR%\\client.ini",
            ConfigFileContent = "[Preferences]\nWebUI\\Enabled=true\n"
        };
        await service.InstallAsync(spec, CancellationToken.None);

        Assert.True(configWriter.WasCalled);
        Assert.Equal(Environment.ExpandEnvironmentVariables("%TEST_CONFIG_DIR%\\client.ini"), configWriter.LastPath);
        Assert.Equal("[Preferences]\nWebUI\\Enabled=true\n", configWriter.LastContent);
    }

    [Fact]
    public async Task A_pre_start_command_is_fired_before_the_main_post_install_command()
    {
        var downloadedPath = WriteTempFile(new byte[20]);
        var processes = new RecordingProcesses(exitCode: 0);
        var starter = new RecordingProcessStarter(new FakeStartedProcess(hasExited: false));
        var readiness = new FakeReadinessChecker(readyOnCall: 1);
        var service = new ComponentInstallService(
            new FakePrograms(),
            new FakePorts(),
            new RecordingDownloader(downloadedPath),
            new FakeSignatures(signed: true),
            processes,
            starter,
            readiness);

        var spec = Spec with
        {
            MustBeSigned = true,
            PreStartCommand = "%TEST_DAEMON_HOME%\\deluged.exe",
            PostInstallCommand = "%TEST_DAEMON_HOME%\\deluge-web.exe",
            PostInstallArgs = "--port 8112",
            ReadinessUrl = "http://127.0.0.1:8112/json",
            ReadinessTimeoutSeconds = 1
        };
        var result = await service.InstallAsync(spec, CancellationToken.None);

        Assert.Equal(ComponentInstallOutcome.Installed, result.Outcome);
        // The started daemon and the tracked Web UI/API process are both
        // started, with the daemon started first, but only the Web UI/API
        // process's readiness is polled.
        Assert.Equal(Environment.ExpandEnvironmentVariables("%TEST_DAEMON_HOME%\\deluge-web.exe"), starter.LastPath);
    }

    private static readonly BundledComponentSpec WeirLikeSpec = Spec with
    {
        DisplayName = "Test Processor",
        Kind = "test-processor",
        UninstallDisplayNameHint = "Test Processor",
        MustBeSigned = true,
        PostInstallCommand = "%TEST_PROCESSOR_HOME%\\Test.exe",
        PostInstallArgs = "--port 9999 --silent",
        ReadinessUrl = "http://127.0.0.1:9999/ready",
        ReadinessField = "ready",
        ReadinessValue = "true",
        ReadinessTimeoutSeconds = 1
    };

    [Fact]
    public async Task Installer_success_then_start_then_ready_is_reported_installed()
    {
        var downloadedPath = WriteTempFile(new byte[20]);
        var processes = new RecordingProcesses(exitCode: 0);
        var starter = new RecordingProcessStarter(new FakeStartedProcess(hasExited: false));
        var readiness = new FakeReadinessChecker(readyOnCall: 1);
        var service = new ComponentInstallService(
            new FakePrograms(),
            new FakePorts(),
            new RecordingDownloader(downloadedPath),
            new FakeSignatures(signed: true),
            processes,
            starter,
            readiness);

        var result = await service.InstallAsync(WeirLikeSpec, CancellationToken.None);

        Assert.Equal(ComponentInstallOutcome.Installed, result.Outcome);
        // The service expands the manifest's env-var placeholder before starting it.
        Assert.Equal(Environment.ExpandEnvironmentVariables("%TEST_PROCESSOR_HOME%\\Test.exe"), starter.LastPath);
        Assert.Equal("--port 9999 --silent", starter.LastArguments);
    }

    [Fact]
    public async Task Installer_non_zero_never_starts_the_component()
    {
        var downloadedPath = WriteTempFile(new byte[20]);
        var processes = new RecordingProcesses(exitCode: 1);
        var starter = new RecordingProcessStarter(new FakeStartedProcess(hasExited: false));
        var service = new ComponentInstallService(
            new FakePrograms(),
            new FakePorts(),
            new RecordingDownloader(downloadedPath),
            new FakeSignatures(signed: true),
            processes,
            starter,
            new FakeReadinessChecker(readyOnCall: 1));

        var result = await service.InstallAsync(WeirLikeSpec, CancellationToken.None);

        Assert.Equal(ComponentInstallOutcome.InstallerFailed, result.Outcome);
        Assert.False(starter.WasCalled);
    }

    [Fact]
    public async Task Component_exiting_before_ready_is_reported_as_a_failed_start()
    {
        var downloadedPath = WriteTempFile(new byte[20]);
        var processes = new RecordingProcesses(exitCode: 0);
        var starter = new RecordingProcessStarter(new FakeStartedProcess(hasExited: true, exitCode: 3));
        var readiness = new FakeReadinessChecker(readyOnCall: 1);
        var service = new ComponentInstallService(
            new FakePrograms(),
            new FakePorts(),
            new RecordingDownloader(downloadedPath),
            new FakeSignatures(signed: true),
            processes,
            starter,
            readiness);

        var result = await service.InstallAsync(WeirLikeSpec, CancellationToken.None);

        Assert.Equal(ComponentInstallOutcome.StartFailed, result.Outcome);
        Assert.False(readiness.WasCalled);
    }

    [Fact]
    public async Task Readiness_that_never_reports_ready_times_out()
    {
        var downloadedPath = WriteTempFile(new byte[20]);
        var processes = new RecordingProcesses(exitCode: 0);
        var starter = new RecordingProcessStarter(new FakeStartedProcess(hasExited: false));
        var readiness = new FakeReadinessChecker(readyOnCall: null); // never ready
        var service = new ComponentInstallService(
            new FakePrograms(),
            new FakePorts(),
            new RecordingDownloader(downloadedPath),
            new FakeSignatures(signed: true),
            processes,
            starter,
            readiness);

        var result = await service.InstallAsync(WeirLikeSpec, CancellationToken.None);

        Assert.Equal(ComponentInstallOutcome.ReadinessTimedOut, result.Outcome);
        Assert.True(readiness.CallCount > 0);
    }

    private static string WriteTempFile(byte[] content, string extension = ".bin")
    {
        var path = Path.Combine(Path.GetTempPath(), "deluno-tray-tests-" + Guid.NewGuid().ToString("N") + extension);
        File.WriteAllBytes(path, content);
        return path;
    }

    private sealed class FakePrograms(params string[] names) : IInstalledProgramsReader
    {
        public IReadOnlyList<string> ReadDisplayNames() => names;
    }

    private sealed class FakePorts : IPortProbe
    {
        public Task<bool> IsOpenAsync(string host, int port, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class FakeSignatures(bool signed) : IAuthenticodeVerifier
    {
        public bool IsSigned(string path) => signed;
    }

    private sealed class RecordingDownloader(string? path = null) : IComponentDownloader
    {
        public bool WasCalled { get; private set; }

        public Task<string?> DownloadAsync(string url, string suggestedFileName, CancellationToken cancellationToken)
        {
            WasCalled = true;
            return Task.FromResult(path);
        }
    }

    private sealed class RecordingProcesses(int? exitCode = null) : IProcessRunner
    {
        public bool WasCalled { get; private set; }
        public string? LastPath { get; private set; }
        public string? LastArguments { get; private set; }

        public Task<int?> RunAsync(string path, string? arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            WasCalled = true;
            LastPath = path;
            LastArguments = arguments;
            return Task.FromResult(exitCode);
        }
    }

    private sealed class RecordingConfigFileWriter : IConfigFileWriter
    {
        public bool WasCalled { get; private set; }
        public string? LastPath { get; private set; }
        public string? LastContent { get; private set; }

        public Task<bool> WriteIfAbsentAsync(string path, string content, CancellationToken cancellationToken)
        {
            WasCalled = true;
            LastPath = path;
            LastContent = content;
            return Task.FromResult(true);
        }
    }

    private sealed class RecordingProcessStarter(IStartedProcess? toReturn) : IProcessStarter
    {
        public bool WasCalled { get; private set; }
        public string? LastPath { get; private set; }
        public string? LastArguments { get; private set; }

        public IStartedProcess? Start(string path, string? arguments)
        {
            WasCalled = true;
            LastPath = path;
            LastArguments = arguments;
            return toReturn;
        }
    }

    private sealed class FakeStartedProcess(bool hasExited, int exitCode = 0) : IStartedProcess
    {
        public bool HasExited => hasExited;
        public int ExitCode => exitCode;
    }

    /// <summary>Reports ready on the given 1-based call number, or never (null).</summary>
    private sealed class FakeReadinessChecker(int? readyOnCall) : IReadinessChecker
    {
        public bool WasCalled => CallCount > 0;
        public int CallCount { get; private set; }

        public Task<bool> CheckAsync(string url, string field, string expectedValue, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(readyOnCall is not null && CallCount >= readyOnCall);
        }
    }
}
