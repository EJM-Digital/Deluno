using Deluno.Companions;
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
    static ComponentInstallServiceTests()
    {
        // A started client is remembered in a file under LocalAppData; keep the
        // fixture clients out of the real profile.
        Environment.SetEnvironmentVariable("DELUNO_TEST_LOCALAPPDATA",
            Path.Combine(Path.GetTempPath(), "deluno-install-tests-" + Guid.NewGuid().ToString("N")));
    }

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

        var result = await service.InstallAsync(Spec with { MustBeSigned = true, ExpectedPublisher = "Test Publisher" }, CancellationToken.None);

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

        var result = await service.InstallAsync(Spec with { MustBeSigned = true, ExpectedPublisher = "Test Publisher" }, CancellationToken.None);

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

        var result = await service.InstallAsync(Spec with { MustBeSigned = true, ExpectedPublisher = "Test Publisher" }, CancellationToken.None);

        Assert.Equal(ComponentInstallOutcome.Installed, result.Outcome);
        Assert.True(processes.WasCalled);
        // Run from the locked folder, never straight from the download (#1061).
        Assert.Equal(ElevatedInstallScript.PowerShellPath, processes.LastPath);
        Assert.Contains("-ArgumentList '/S'", Script(processes.LastArguments!));
    }

    private static string Script(string arguments)
        => System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(arguments[(arguments.IndexOf("-EncodedCommand ", StringComparison.Ordinal) + 16)..]));

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

        var result = await service.InstallAsync(Spec with { MustBeSigned = true, ExpectedPublisher = "Test Publisher" }, CancellationToken.None);

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

        var result = await service.InstallAsync(Spec with { MustBeSigned = true, ExpectedPublisher = "Test Publisher", FileName = "test-client.msi" }, CancellationToken.None);

        Assert.Equal(ComponentInstallOutcome.Installed, result.Outcome);
        // msiexec runs from inside the elevated PowerShell step (#1061), on a protected copy.
        Assert.Equal(ElevatedInstallScript.PowerShellPath, processes.LastPath);
        var script = Script(processes.LastArguments!);
        Assert.Contains(@"System32\msiexec.exe", script);
        Assert.Contains("'" + downloadedPath + "'", script);
        Assert.Contains("ALLUSERS=1", script);
        Assert.Contains("/S", script); // the manifest's own silentArgs still applies
        Assert.Contains("/l*v", script);
    }

    [Fact]
    public async Task A_vendor_exe_is_run_from_the_locked_folder_and_never_straight_from_the_download()
    {
        var downloadedPath = WriteTempFile(new byte[20], ".exe");
        var processes = new RecordingProcesses(exitCode: 0);
        var service = new ComponentInstallService(
            new FakePrograms(),
            new FakePorts(),
            new RecordingDownloader(downloadedPath),
            new FakeSignatures(signed: true),
            processes);

        await service.InstallAsync(Spec with { MustBeSigned = true, ExpectedPublisher = "Test Publisher" }, CancellationToken.None);

        Assert.NotEqual(downloadedPath, processes.LastPath);
        Assert.Equal(ElevatedInstallScript.PowerShellPath, processes.LastPath);
        var script = Script(processes.LastArguments!);
        Assert.Contains("Copy-Item -LiteralPath '" + downloadedPath + "'", script);
        Assert.Contains("Start-Process -FilePath $copy", script);
        Assert.Contains("Get-AuthenticodeSignature -LiteralPath $copy", script);
        Assert.Contains("Test-Publisher $sig.SignerCertificate 'Test Publisher'", script);
        Assert.True(script.IndexOf("Get-FileHash", StringComparison.Ordinal) < script.IndexOf("Start-Process", StringComparison.Ordinal));
        Assert.Contains("Remove-Item -LiteralPath $fresh -Recurse", script);
    }

    [Fact]
    public async Task A_pinned_exe_is_run_from_the_locked_folder_after_its_hash_is_checked_there()
    {
        var downloadedPath = WriteTempFile(new byte[20], ".exe");
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(downloadedPath)));
        var processes = new RecordingProcesses(exitCode: 0);
        var service = new ComponentInstallService(new FakePrograms(), new FakePorts(), new RecordingDownloader(downloadedPath), new FakeSignatures(signed: false), processes);

        var result = await service.InstallAsync(Spec with { Sha256 = expected }, CancellationToken.None);

        Assert.Equal(ComponentInstallOutcome.Installed, result.Outcome);
        var script = Script(processes.LastArguments!);
        Assert.Contains($"'{expected}'", script);
        Assert.DoesNotContain("Get-AuthenticodeSignature", script);
        Assert.Contains("Start-Process -FilePath $copy", script);
    }

    [Fact]
    public async Task An_installer_that_installs_for_the_signed_in_person_alone_is_not_raised_by_Deluno()
    {
        var downloadedPath = WriteTempFile(new byte[20], ".exe");
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(downloadedPath)));
        var processes = new RecordingProcesses(exitCode: 0);
        var service = new ComponentInstallService(new FakePrograms(), new FakePorts(), new RecordingDownloader(downloadedPath), new FakeSignatures(signed: false), processes);

        await service.InstallAsync(Spec with { Sha256 = expected, RunsElevated = false }, CancellationToken.None);

        Assert.Equal(downloadedPath, processes.LastPath);
        Assert.Equal("/S", processes.LastArguments);
    }

    [Fact]
    public async Task A_signature_rule_that_names_no_publisher_and_has_no_checksum_is_refused()
    {
        var downloadedPath = WriteTempFile(new byte[20], ".exe");
        var processes = new RecordingProcesses(exitCode: 0);
        var service = new ComponentInstallService(new FakePrograms(), new FakePorts(), new RecordingDownloader(downloadedPath), new FakeSignatures(signed: true), processes);

        var result = await service.InstallAsync(Spec with { MustBeSigned = true, ExpectedPublisher = null }, CancellationToken.None);

        Assert.Equal(ComponentInstallOutcome.VerificationFailed, result.Outcome);
        Assert.False(processes.WasCalled);
    }

    [Fact]
    public async Task A_prerequisite_signature_rule_that_names_no_publisher_is_refused()
    {
        var downloader = new SequencedDownloader(WriteTempFile(new byte[20]), WriteTempFile(new byte[20]));
        var processes = new RecordingProcesses(exitCode: 0);
        var service = new ComponentInstallService(new FakePrograms(), new FakePorts(), downloader, new FakeSignatures(signed: true), processes,
            prerequisiteRegistry: new FakePrerequisiteRegistry(present: false));

        var result = await service.InstallAsync(Spec with { MustBeSigned = true, ExpectedPublisher = "Test Publisher", Prerequisite = Prerequisite with { ExpectedPublisher = null } }, CancellationToken.None);

        Assert.Equal(ComponentInstallOutcome.VerificationFailed, result.Outcome);
        Assert.False(processes.WasCalled);
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
            MustBeSigned = true, ExpectedPublisher = "Test Publisher",
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
            MustBeSigned = true, ExpectedPublisher = "Test Publisher",
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
        MustBeSigned = true, ExpectedPublisher = "Test Publisher",
        PostInstallCommand = "%TEST_PROCESSOR_HOME%\\Test.exe",
        PostInstallArgs = "--port 9999 --silent",
        ReadinessUrl = "http://127.0.0.1:9999/ready",
        ReadinessField = "ready",
        ReadinessValue = "true",
        ReadinessTimeoutSeconds = 1
    };

    [Fact]
    public async Task While_Weir_is_starting_the_progress_says_so_in_plain_words_and_other_components_do_not()
    {
        var weirSteps = new List<ComponentProgress>();
        var otherSteps = new List<ComponentProgress>();
        foreach (var (spec, steps) in new[] { (WeirLikeSpec with { Kind = "weir", DisplayName = "Weir" }, weirSteps), (WeirLikeSpec, otherSteps) })
        {
            var service = new ComponentInstallService(
                new FakePrograms(), new FakePorts(), new RecordingDownloader(WriteTempFile(new byte[20])), new FakeSignatures(signed: true),
                new RecordingProcesses(exitCode: 0), new RecordingProcessStarter(new FakeStartedProcess(hasExited: false)),
                new FakeReadinessChecker(readyOnCall: 1));

            await service.InstallAsync(spec, CancellationToken.None, steps.Add);
        }

        var weirStart = Assert.Single(weirSteps, step => step.Stage == ComponentStage.Starting);
        Assert.Equal("Weir is starting; this can take a few minutes after an update.", weirStart.Detail);
        Assert.Contains("Weir is starting; this can take a few minutes after an update", ComponentPickerDialog.Describe(weirStart));
        var otherStart = Assert.Single(otherSteps, step => step.Stage == ComponentStage.Starting);
        Assert.DoesNotContain("Weir", ComponentPickerDialog.Describe(otherStart));
    }

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

    private static readonly BundledComponentPrerequisite Prerequisite = new(
        Name: "Test Runtime",
        Url: "https://example.invalid/test-runtime.exe",
        FileName: "test-runtime.exe",
        MinimumBytes: 10,
        MustBeSigned: true,
        SilentArgs: "/install /quiet /norestart",
        SkipRegistryKeyPath: "SOFTWARE\\Test\\Runtime",
        SkipRegistryValueName: "Installed",
        ExpectedPublisher: "Test Publisher");

    [Fact]
    public async Task A_present_prerequisite_is_skipped_without_downloading_anything()
    {
        var mainDownload = WriteTempFile(new byte[20]);
        var downloader = new RecordingDownloader(mainDownload);
        var processes = new RecordingProcesses(exitCode: 0);
        var service = new ComponentInstallService(
            new FakePrograms(),
            new FakePorts(),
            downloader,
            new FakeSignatures(signed: true),
            processes,
            prerequisiteRegistry: new FakePrerequisiteRegistry(present: true));

        var result = await service.InstallAsync(Spec with { MustBeSigned = true, ExpectedPublisher = "Test Publisher", Prerequisite = Prerequisite }, CancellationToken.None);

        Assert.Equal(ComponentInstallOutcome.Installed, result.Outcome);
        // Only the component's own installer ran -- the prerequisite's own
        // download URL was never fetched because it was already present.
        Assert.Equal(Spec.Url, downloader.LastUrl);
    }

    [Fact]
    public async Task A_missing_prerequisite_is_downloaded_verified_and_run_before_the_component()
    {
        var mainDownload = WriteTempFile(new byte[20]);
        var prereqDownload = WriteTempFile(new byte[20]);
        // InstallAsync downloads and verifies the component's own installer
        // first, then handles its prerequisite, so the component's URL is
        // resolved before the prerequisite's own.
        var downloader = new SequencedDownloader(mainDownload, prereqDownload);
        var processes = new RecordingProcesses(exitCode: 0);
        var service = new ComponentInstallService(
            new FakePrograms(),
            new FakePorts(),
            downloader,
            new FakeSignatures(signed: true),
            processes,
            prerequisiteRegistry: new FakePrerequisiteRegistry(present: false));

        var result = await service.InstallAsync(Spec with { MustBeSigned = true, ExpectedPublisher = "Test Publisher", Prerequisite = Prerequisite }, CancellationToken.None);

        Assert.Equal(ComponentInstallOutcome.Installed, result.Outcome);
        Assert.Equal(2, downloader.CallCount);
        Assert.Equal(Spec.Url, downloader.Urls[0]);
        Assert.Equal(Prerequisite.Url, downloader.Urls[1]);
        // The runtime goes through the same locked-folder run, re-hashed and re-verified on the exact copy that runs.
        var prerequisiteScript = Script(processes.Arguments[0]!);
        Assert.Equal(ElevatedInstallScript.PowerShellPath, processes.Paths[0]);
        Assert.Contains("Get-FileHash", prerequisiteScript);
        Assert.Contains("Test-Publisher $sig.SignerCertificate 'Test Publisher'", prerequisiteScript);
        Assert.Contains("-ArgumentList '/install /quiet /norestart'", prerequisiteScript);
        Assert.Contains("@(3010) -contains $code", prerequisiteScript);
    }

    [Fact]
    public async Task An_unsigned_prerequisite_is_refused_and_the_component_never_installs()
    {
        var mainDownload = WriteTempFile(new byte[20]);
        var prereqDownload = WriteTempFile(new byte[20]);
        // InstallAsync verifies the component's own installer first, so only
        // the prerequisite's file needs to be unsigned to reach this path.
        var downloader = new SequencedDownloader(mainDownload, prereqDownload);
        var processes = new RecordingProcesses(exitCode: 0);
        var service = new ComponentInstallService(
            new FakePrograms(),
            new FakePorts(),
            downloader,
            new SignedForPaths(mainDownload),
            processes,
            prerequisiteRegistry: new FakePrerequisiteRegistry(present: false));

        var result = await service.InstallAsync(Spec with { MustBeSigned = true, ExpectedPublisher = "Test Publisher", Prerequisite = Prerequisite }, CancellationToken.None);

        Assert.Equal(ComponentInstallOutcome.VerificationFailed, result.Outcome);
        Assert.False(processes.WasCalled);
    }

    [Fact]
    public async Task A_prerequisite_exit_code_3010_reboot_required_still_counts_as_success()
    {
        var mainDownload = WriteTempFile(new byte[20]);
        var prereqDownload = WriteTempFile(new byte[20]);
        var downloader = new SequencedDownloader(mainDownload, prereqDownload);
        var processes = new SequencedExitCodeProcesses(3010, 0);
        var service = new ComponentInstallService(
            new FakePrograms(),
            new FakePorts(),
            downloader,
            new FakeSignatures(signed: true),
            processes,
            prerequisiteRegistry: new FakePrerequisiteRegistry(present: false));

        var result = await service.InstallAsync(Spec with { MustBeSigned = true, ExpectedPublisher = "Test Publisher", Prerequisite = Prerequisite }, CancellationToken.None);

        Assert.Equal(ComponentInstallOutcome.Installed, result.Outcome);
    }

    [Fact]
    public async Task A_prerequisite_that_fails_to_install_stops_the_component_installing()
    {
        var mainDownload = WriteTempFile(new byte[20]);
        var prereqDownload = WriteTempFile(new byte[20]);
        var downloader = new SequencedDownloader(mainDownload, prereqDownload);
        var processes = new SequencedExitCodeProcesses(1603, 0);
        var service = new ComponentInstallService(
            new FakePrograms(),
            new FakePorts(),
            downloader,
            new FakeSignatures(signed: true),
            processes,
            prerequisiteRegistry: new FakePrerequisiteRegistry(present: false));

        var result = await service.InstallAsync(Spec with { MustBeSigned = true, ExpectedPublisher = "Test Publisher", Prerequisite = Prerequisite }, CancellationToken.None);

        Assert.Equal(ComponentInstallOutcome.InstallerFailed, result.Outcome);
        // Only the prerequisite ran -- the component's own installer never got a turn.
        Assert.Equal(1, processes.RunCount);
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

    /// <summary>Signed only for the given path(s) -- everything else answers false, for telling a prerequisite's own file apart from the component's.</summary>
    private sealed class SignedForPaths(params string[] signedPaths) : IAuthenticodeVerifier
    {
        public bool IsSigned(string path) => signedPaths.Contains(path);
    }

    private sealed class RecordingDownloader(string? path = null) : IComponentDownloader
    {
        public bool WasCalled { get; private set; }
        public string? LastUrl { get; private set; }

        public Task<string?> DownloadAsync(string url, string suggestedFileName, CancellationToken cancellationToken)
        {
            WasCalled = true;
            LastUrl = url;
            return Task.FromResult(path);
        }
    }

    /// <summary>Answers each call's download from a fixed list, in order -- for a prerequisite download followed by the component's own.</summary>
    private sealed class SequencedDownloader(params string?[] paths) : IComponentDownloader
    {
        public int CallCount { get; private set; }
        public List<string> Urls { get; } = [];

        public Task<string?> DownloadAsync(string url, string suggestedFileName, CancellationToken cancellationToken)
        {
            Urls.Add(url);
            var path = CallCount < paths.Length ? paths[CallCount] : null;
            CallCount++;
            return Task.FromResult(path);
        }
    }

    /// <summary>Answers each call's exit code from a fixed list, in order -- for a prerequisite run followed by the component's own.</summary>
    private sealed class SequencedExitCodeProcesses(params int?[] exitCodes) : IProcessRunner
    {
        public int RunCount { get; private set; }

        public Task<int?> RunAsync(string path, string? arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var exitCode = RunCount < exitCodes.Length ? exitCodes[RunCount] : null;
            RunCount++;
            return Task.FromResult(exitCode);
        }
    }

    private sealed class FakePrerequisiteRegistry(bool present) : IPrerequisiteRegistryReader
    {
        public bool IsPresent(string subKeyPath, string valueName) => present;
    }

    private sealed class RecordingProcesses(int? exitCode = null) : IProcessRunner
    {
        public bool WasCalled { get; private set; }
        public string? LastPath { get; private set; }
        public string? LastArguments { get; private set; }
        public List<string> Paths { get; } = [];
        public List<string?> Arguments { get; } = [];

        public Task<int?> RunAsync(string path, string? arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            WasCalled = true;
            LastPath = path;
            LastArguments = arguments;
            Paths.Add(path);
            Arguments.Add(arguments);
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
