using Deluno.Companions;
using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>#1061: what the installer logs it checked for each component, and the folder it no longer leaves behind.</summary>
public sealed partial class ComponentSetupTests
{
    private sealed class RecordingInstallLog : IInstallLog
    {
        public List<string> Lines { get; } = [];
        public void Write(string line) => Lines.Add(line);
    }

    private sealed class PresentPrerequisite(bool present) : IPrerequisiteRegistryReader
    {
        public bool IsPresent(string subKeyPath, string valueName) => present;
    }

    private static ComponentInstallService ServiceWithLog(BundledComponentSpec spec, RecordingInstallLog log, IAuthenticodeVerifier verifier, bool prerequisitePresent)
        => new(new FakePrograms(), new FakePorts(), new FixedDownloader(WriteTempFile(new byte[20], ".msi")), verifier, new FixedExitRunner(0),
            configWriter: new FakeConfigWriter(true), credentialStore: new RecordingCredentialStore(), startedComponents: new FakeRegistry(),
            prerequisiteRegistry: new PresentPrerequisite(prerequisitePresent), installLog: log);

    [Fact]
    public async Task The_install_log_says_which_signature_and_publisher_were_checked_for_the_app_and_its_prerequisite()
    {
        var spec = Load("transmission") with { Sha256 = null, MustBeSigned = true, MinimumBytes = 1, PostInstallCommand = null, ReadinessUrl = null,
            Prerequisite = Load("transmission").Prerequisite! with { MinimumBytes = 1 } };
        var log = new RecordingInstallLog();

        await ServiceWithLog(spec, log, new AlwaysSigned(), prerequisitePresent: false).InstallAsync(spec, CancellationToken.None);

        Assert.Contains(log.Lines, line => line == $"{spec.DisplayName}: signature valid, publisher {spec.ExpectedPublisher}");
        Assert.Contains(log.Lines, line => line == $"{spec.Prerequisite!.Name}: signature valid, publisher {spec.Prerequisite.ExpectedPublisher}");
    }

    [Fact]
    public async Task The_install_log_says_when_a_pinned_checksum_was_the_check()
    {
        var download = WriteTempFile(new byte[20], ".exe");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(download)));
        var spec = Load("deluge") with { Url = "https://example.invalid/d.exe", GithubRepo = null, DirectoryListingUrl = null, Sha256 = hash, MinimumBytes = 1, Prerequisite = null, PostInstallCommand = null, ReadinessUrl = null };
        var log = new RecordingInstallLog();
        var service = new ComponentInstallService(new FakePrograms(), new FakePorts(), new FixedDownloader(download), new AlwaysSigned(), new FixedExitRunner(0),
            configWriter: new FakeConfigWriter(true), credentialStore: new RecordingCredentialStore(), startedComponents: new FakeRegistry(), installLog: log);

        await service.InstallAsync(spec, CancellationToken.None);

        Assert.Contains(log.Lines, line => line == $"{spec.DisplayName}: SHA-256 matches the checksum pinned in Deluno's list of components");
        // The run record names the installer that ran by its hash, since the file is deleted afterwards (#1202).
        Assert.Contains(log.Lines, line => line.StartsWith($"{spec.DisplayName}: installer ", StringComparison.Ordinal)
            && line.EndsWith($" sha256 {hash}", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_install_log_records_a_refused_installer()
    {
        var spec = Load("transmission") with { Sha256 = "00", MinimumBytes = 1, Prerequisite = null, PostInstallCommand = null, ReadinessUrl = null };
        var log = new RecordingInstallLog();

        await ServiceWithLog(spec, log, new AlwaysSigned(), prerequisitePresent: true).InstallAsync(spec, CancellationToken.None);

        Assert.Contains(log.Lines, line => line.StartsWith($"{spec.DisplayName}: refused.", StringComparison.Ordinal));
    }

    [Fact]
    public void The_elevated_script_removes_the_installer_parent_folder_only_when_it_is_empty_and_only_after_the_run_folder()
    {
        var script = ElevatedInstallScript.Build("C:\\dl\\a.msi", "ABCD", "/qn", null);

        int At(string text) => script.IndexOf(text, StringComparison.Ordinal);
        Assert.True(At("Remove-Item -LiteralPath $fresh") < At("Remove-Item -LiteralPath $root -Force"));
        Assert.Contains("-not (Get-ChildItem -LiteralPath $root -Force", script);
    }

    [Fact]
    public void After_a_run_the_installer_parent_folder_is_gone_not_left_empty()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var parent = Path.Combine(Path.GetTempPath(), "deluno-parent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        try
        {
            // The wrong hash stops the run at the check, which still ends in the same clean-up.
            var download = WriteTempFile(new byte[20], ".msi");
            var exit = RunPowerShell(ElevatedInstallScript.Build(download, "ABCD", "/qn", null, baseFolder: parent));

            // Not elevated here, the lock-down step may refuse first; either way nothing is left behind.
            Assert.Contains(exit, new[] { ElevatedInstallScript.HashMismatchExitCode, ElevatedInstallScript.FolderExitCode });
            Assert.Empty(Directory.GetFileSystemEntries(parent));
        }
        finally
        {
            try { Directory.Delete(parent, true); } catch { }
        }
    }
}
