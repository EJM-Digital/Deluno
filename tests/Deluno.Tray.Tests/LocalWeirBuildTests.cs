using Deluno.Companions;
using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>#1158: the test-only override that installs a local Weir build instead of Weir's newest release.</summary>
public sealed partial class ComponentSetupTests
{
    private sealed class MustNotDownload : IComponentDownloader
    {
        public Task<string?> DownloadAsync(string url, string suggestedFileName, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The local Weir build must be used without any download.");
    }

    private static ComponentInstallService ServiceUsing(LocalWeirBuild? local, RecordingInstallLog log)
        => new(new FakePrograms(), new FakePorts(), new MustNotDownload(), new AlwaysSigned(), new FixedExitRunner(0),
            configWriter: new FakeConfigWriter(true), credentialStore: new RecordingCredentialStore(), startedComponents: new FakeRegistry(),
            installLog: log, localBuild: _ => local);

    private static BundledComponentSpec WeirWithoutHooks()
        => Load("weir") with { MinimumBytes = 1, PostInstallCommand = null, ReadinessUrl = null, Prerequisite = null };

    [Fact]
    public void The_override_is_read_from_the_environment_or_a_file_beside_the_tray_and_only_for_Weir()
    {
        var variables = new Dictionary<string, string?>
        {
            [LocalWeirBuild.PathVariable] = @"C:\builds\Weir-win-Setup.exe",
            [LocalWeirBuild.Sha256Variable] = "ABC123",
        };
        var fromEnvironment = LocalWeirBuild.Find("weir", name => variables.GetValueOrDefault(name), directoryBesideTray: null);
        Assert.Equal(new LocalWeirBuild(@"C:\builds\Weir-win-Setup.exe", "ABC123"), fromEnvironment);
        Assert.Null(LocalWeirBuild.Find("qbittorrent", name => variables.GetValueOrDefault(name), directoryBesideTray: null));

        var folder = Directory.CreateTempSubdirectory("deluno-local-weir-").FullName;
        File.WriteAllText(Path.Combine(folder, LocalWeirBuild.FileName), """{ "path": "D:/x/Weir-win-Setup.exe", "sha256": "DEF456" }""");
        var fromFile = LocalWeirBuild.Find("weir", _ => null, folder);
        Assert.Equal(new LocalWeirBuild("D:/x/Weir-win-Setup.exe", "DEF456"), fromFile);

        variables.Remove(LocalWeirBuild.Sha256Variable);
        Assert.Null(LocalWeirBuild.Find("weir", name => variables.GetValueOrDefault(name), directoryBesideTray: null));
        Assert.Null(LocalWeirBuild.Find("weir", _ => null, Path.Combine(folder, "nowhere")));
    }

    [Fact]
    public async Task A_local_Weir_build_is_installed_instead_of_the_newest_release_and_the_log_says_so()
    {
        var local = WriteTempFile(new byte[20], ".exe");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(local)));
        var log = new RecordingInstallLog();

        var result = await ServiceUsing(new LocalWeirBuild(local, hash), log).InstallAsync(WeirWithoutHooks(), CancellationToken.None);

        Assert.Equal(ComponentInstallOutcome.Installed, result.Outcome);
        Assert.Contains(log.Lines, line => line.StartsWith("Weir: using local Weir build ") && line.EndsWith(" " + hash));
        Assert.True(File.Exists(local), "the person's own file must not be deleted by the clean-up");
    }

    [Fact]
    public async Task A_local_Weir_build_that_does_not_match_its_checksum_is_refused()
    {
        var local = WriteTempFile(new byte[20], ".exe");
        var log = new RecordingInstallLog();

        var result = await ServiceUsing(new LocalWeirBuild(local, new string('0', 64)), log).InstallAsync(WeirWithoutHooks(), CancellationToken.None);

        Assert.Equal(ComponentInstallOutcome.VerificationFailed, result.Outcome);
        Assert.True(File.Exists(local));
    }

    [Fact]
    public async Task A_local_Weir_build_that_is_not_there_is_reported_not_replaced_by_a_download()
    {
        var result = await ServiceUsing(new LocalWeirBuild(Path.Combine(Path.GetTempPath(), "no-such-weir.exe"), "00"), new RecordingInstallLog())
            .InstallAsync(WeirWithoutHooks(), CancellationToken.None);

        Assert.Equal(ComponentInstallOutcome.DownloadFailed, result.Outcome);
    }
}
