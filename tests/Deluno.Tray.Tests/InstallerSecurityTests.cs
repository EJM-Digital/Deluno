using System.Text;
using System.Text.Json;
using Deluno.Api.Network;
using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>#1061: what the installer seeds must keep every client's protections on, and the elevated step must not run a file someone could swap.</summary>
public sealed partial class ComponentSetupTests
{
    private static string Seeded(string kind)
    {
        var spec = Load(kind);
        var credentials = new BundledComponentCredentials(spec.CredentialsUsername ?? string.Empty, "Pa55w0rdGenerated");
        return ComponentConfig.RenderPlaceholders(spec.ConfigFileContent!, credentials);
    }

    [Fact]
    public void Seeded_qBittorrent_keeps_every_protection_on()
    {
        var ini = Seeded("qbittorrent");

        Assert.Contains("WebUI\\LocalHostAuth=true", ini);
        Assert.Contains("WebUI\\CSRFProtection=true", ini);
        Assert.Contains("WebUI\\HostHeaderValidation=true", ini);
        Assert.Contains("WebUI\\AuthSubnetWhitelistEnabled=false", ini);
        Assert.DoesNotContain("AuthSubnetWhitelist=", ini);
        Assert.DoesNotContain("LocalHostAuth=false", ini);
        Assert.DoesNotContain("CSRFProtection=false", ini);
        Assert.DoesNotContain("HostHeaderValidation=false", ini);
        // Host-header validation only bites when the allowed names are listed; the default is "*".
        Assert.Contains("WebUI\\ServerDomains=localhost;127.0.0.1", ini);
        Assert.Contains("WebUI\\Address=127.0.0.1", ini);
    }

    [Fact]
    public void Seeded_Transmission_requires_a_generated_sign_in()
    {
        using var json = JsonDocument.Parse(Seeded("transmission"));
        var root = json.RootElement;

        Assert.True(root.GetProperty("rpc-authentication-required").GetBoolean());
        Assert.Equal("deluno", root.GetProperty("rpc-username").GetString());
        Assert.Equal("Pa55w0rdGenerated", root.GetProperty("rpc-password").GetString());
        Assert.True(root.GetProperty("rpc-whitelist-enabled").GetBoolean());
        Assert.Equal("127.0.0.1", root.GetProperty("rpc-bind-address").GetString());
    }

    [Fact]
    public void Seeded_SABnzbd_has_a_generated_sign_in_and_a_host_whitelist()
    {
        var ini = Seeded("sabnzbd");

        Assert.Contains("username = deluno", ini);
        Assert.Contains("password = Pa55w0rdGenerated", ini);
        Assert.Contains("host_whitelist = 127.0.0.1,localhost", ini);
        Assert.Contains("host = 127.0.0.1", ini);
        Assert.DoesNotContain("username = \n", ini);
        Assert.DoesNotContain("password = \n", ini);
    }

    [Fact]
    public void Every_download_client_that_seeds_a_sign_in_names_the_user_that_gets_a_generated_password()
    {
        foreach (var kind in new[] { "qbittorrent", "transmission", "sabnzbd", "nzbget", "deluge" })
        {
            Assert.False(string.IsNullOrWhiteSpace(Load(kind).CredentialsUsername), kind);
        }
    }

    [Fact]
    public void An_installer_checked_only_by_its_signature_names_the_publisher_it_must_be_signed_by()
    {
        foreach (var spec in BundledComponents.Load().Where(s => s.MustBeSigned && s.Sha256 is null
            && string.IsNullOrWhiteSpace(s.GithubChecksumAsset) && string.IsNullOrWhiteSpace(s.GithubChecksumAssetPattern)
            && string.IsNullOrWhiteSpace(s.ChecksumSuffix)))
        {
            Assert.False(string.IsNullOrWhiteSpace(spec.ExpectedPublisher), spec.Kind);
        }

        var prerequisite = Load("transmission").Prerequisite!;
        Assert.True(prerequisite.MustBeSigned);
        Assert.Equal("Microsoft Corporation", prerequisite.ExpectedPublisher);
    }

    [Fact]
    public void A_signature_from_the_wrong_publisher_is_refused()
    {
        var verifier = new PublisherOnlyVerifier("Somebody Else");
        var spec = Load("transmission");

        Assert.False(verifier.IsSignedBy("x.msi", spec.ExpectedPublisher));
        Assert.True(new PublisherOnlyVerifier(spec.ExpectedPublisher!).IsSignedBy("x.msi", spec.ExpectedPublisher));
    }

    [Fact]
    public async Task An_msi_is_installed_only_from_a_copy_the_elevated_step_re_hashes()
    {
        var download = WriteTempFile(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 }, ".msi");
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(download)));
        var runner = new ElevatedRecordingRunner();
        var service = new ComponentInstallService(
            new FakePrograms(), new FakePorts(), new FixedDownloader(download), new AlwaysSigned(), runner,
            configWriter: new FakeConfigWriter(true), credentialStore: new RecordingCredentialStore(), startedComponents: new FakeRegistry());

        var spec = Load("transmission") with { Sha256 = null, MustBeSigned = true, MinimumBytes = 1, Prerequisite = null, PostInstallCommand = null, ReadinessUrl = null };
        await service.InstallAsync(spec, CancellationToken.None);

        var arguments = runner.LastArguments!;
        var encoded = arguments[(arguments.IndexOf("-EncodedCommand ", StringComparison.Ordinal) + "-EncodedCommand ".Length)..];
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        Assert.Contains("ProgramData", script);
        Assert.Contains("icacls.exe", script);
        Assert.Contains("/inheritance:r", script);
        Assert.Contains($"'{expected}'", script);
        Assert.Contains($"$code = {ElevatedInstallScript.HashMismatchExitCode}", script);
        // The ACL is set before the file is copied in, and the hash is checked before msiexec runs.
        Assert.True(script.IndexOf("icacls.exe", StringComparison.Ordinal) < script.IndexOf("Copy-Item", StringComparison.Ordinal));
        Assert.True(script.IndexOf("Get-FileHash", StringComparison.Ordinal) < script.IndexOf("msiexec.exe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_elevated_step_that_found_a_different_file_is_reported_as_not_run()
    {
        var download = WriteTempFile(new byte[20], ".msi");
        var runner = new FixedExitRunner(ElevatedInstallScript.HashMismatchExitCode);
        var service = new ComponentInstallService(
            new FakePrograms(), new FakePorts(), new FixedDownloader(download), new AlwaysSigned(), runner,
            configWriter: new FakeConfigWriter(true), credentialStore: new RecordingCredentialStore(), startedComponents: new FakeRegistry());

        var spec = Load("transmission") with { Sha256 = null, MustBeSigned = true, MinimumBytes = 1, Prerequisite = null, PostInstallCommand = null, ReadinessUrl = null };
        var result = await service.InstallAsync(spec, CancellationToken.None);

        Assert.Equal(ComponentInstallOutcome.VerificationFailed, result.Outcome);
    }

    [Fact]
    public void The_elevated_script_makes_a_fresh_locked_folder_before_copying_and_deletes_it_after()
    {
        var script = ElevatedInstallScript.Build("C:\\dl\\a.msi", "ABCD", "/qn", null);

        int At(string text, int from = 0) => script.IndexOf(text, from, StringComparison.Ordinal);
        var linkChecks = At("Test-Link $p");
        var makeFresh = At("New-Item -ItemType Directory -Path $fresh");
        var lockFresh = At("Lock-Folder $fresh");
        var copy = At("Copy-Item");
        var hash = At("Get-FileHash");
        var run = At("msiexec.exe");
        var cleanup = At("Remove-Item -LiteralPath $fresh -Recurse");

        Assert.Contains("[guid]::NewGuid()", script);
        Assert.Contains("/setowner '*S-1-5-32-544'", script);
        Assert.Contains("/inheritance:r", script);
        Assert.Contains("'*S-1-5-18:(OI)(CI)F'", script);
        Assert.DoesNotContain("New-Item -ItemType Directory -Force", script);
        Assert.True(linkChecks >= 0 && linkChecks < makeFresh);
        Assert.True(makeFresh < lockFresh);
        Assert.True(lockFresh < copy);
        Assert.True(copy < hash);
        Assert.True(hash < run);
        Assert.True(run < cleanup);
        // msiexec runs on the copy in the fresh folder, not on the download.
        Assert.Contains("'/i \"' + $copy", script);
        Assert.DoesNotContain("'/i \"C:", script);
    }

    [Fact]
    public void The_elevated_script_checks_ProgramData_and_every_folder_below_it_for_links()
    {
        var script = ElevatedInstallScript.Build("C:\\dl\\a.msi", "ABCD", "/qn", null);

        Assert.Contains("@($programData, $parent, $root)", script);
        Assert.Contains("FileAttributes]::ReparsePoint", script);
        Assert.Contains($"$code = {ElevatedInstallScript.ReparsePointExitCode}", script);
    }

    [Fact]
    public async Task The_elevated_script_refuses_to_run_when_the_installer_folder_is_a_junction()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var programData = Path.Combine(Path.GetTempPath(), "deluno-pd-" + Guid.NewGuid().ToString("N"));
        var elsewhere = Path.Combine(Path.GetTempPath(), "deluno-elsewhere-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(programData, "Deluno"));
        Directory.CreateDirectory(elsewhere);
        var junction = Path.Combine(programData, "Deluno", "installer");
        try
        {
            RunPowerShell($"New-Item -ItemType Junction -Path '{junction}' -Target '{elsewhere}' | Out-Null", null);

            var download = WriteTempFile(new byte[20], ".msi");
            var script = ElevatedInstallScript.Build(download, "ABCD", "/qn", null);
            var exit = RunPowerShell(script, programData);

            Assert.Equal(ElevatedInstallScript.ReparsePointExitCode, exit);
            // Nothing was made inside the junction's target.
            Assert.Empty(Directory.GetFileSystemEntries(elsewhere));
        }
        finally
        {
            try { Directory.Delete(junction); } catch { }
            try { Directory.Delete(programData, true); } catch { }
            try { Directory.Delete(elsewhere, true); } catch { }
        }

        await Task.CompletedTask;
    }

    private static int RunPowerShell(string script, string? programData)
    {
        var info = new System.Diagnostics.ProcessStartInfo("powershell.exe", ElevatedInstallScript.PowerShellArguments(script))
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (programData is not null)
        {
            info.Environment["ProgramData"] = programData;
        }

        using var process = System.Diagnostics.Process.Start(info)!;
        process.WaitForExit(60000);
        return process.ExitCode;
    }

    [Fact]
    public void The_elevated_script_quotes_a_service_name_with_an_apostrophe()
    {
        var script = ElevatedInstallScript.Build("C:\\a'b.msi", "AB", "/qn", "Odd'Name");

        Assert.Contains("'C:\\a''b.msi'", script);
        Assert.Contains("'Odd''Name'", script);
    }

    private sealed class PublisherOnlyVerifier(string publisher) : IAuthenticodeVerifier
    {
        public bool IsSigned(string path) => true;
        public bool IsSignedBy(string path, string? expected) => expected is null || string.Equals(expected, publisher, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FixedExitRunner(int exit) : IProcessRunner
    {
        public Task<int?> RunAsync(string path, string? arguments, TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult<int?>(exit);
        public Task<int?> RunElevatedAsync(string path, string? arguments, TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult<int?>(exit);
    }
}
