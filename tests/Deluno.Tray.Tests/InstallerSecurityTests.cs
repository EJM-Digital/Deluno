using Deluno.Companions;
using System.Text;
using System.Text.Json;
using Deluno.Api.Network;
using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>#1061: what the installer seeds never switches an app's own protection off or adds a password an app would not have, and the elevated step must not run a file someone could swap.</summary>
public sealed partial class ComponentSetupTests
{
    private static string Seeded(string kind)
    {
        var spec = Load(kind);
        var credentials = new BundledComponentCredentials(spec.CredentialsUsername ?? string.Empty, "Pa55w0rdGenerated");
        return ComponentConfig.RenderPlaceholders(spec.ConfigFileContent!, credentials);
    }

    [Fact]
    public void Seeded_qBittorrent_adds_only_the_sign_in_it_needs_and_writes_no_protection()
    {
        var ini = Seeded("qbittorrent");

        // qBittorrent 5.x refuses to start its Web UI without credentials, so these two are added.
        Assert.Contains("WebUI\\Username=deluno", ini);
        Assert.Contains("WebUI\\Password_PBKDF2=", ini);
        Assert.Contains("WebUI\\Address=127.0.0.1", ini);
        // Every protection stays at qBittorrent's own default: Deluno never writes one, on or off.
        foreach (var setting in new[] { "LocalHostAuth", "CSRFProtection", "HostHeaderValidation", "AuthSubnetWhitelist", "ServerDomains", "ClickjackingProtection", "SecureCookie" })
        {
            Assert.DoesNotContain(setting, ini);
        }
    }

    [Fact]
    public void Seeded_Transmission_keeps_its_own_defaults_and_adds_no_password()
    {
        using var json = JsonDocument.Parse(Seeded("transmission"));
        var root = json.RootElement;

        Assert.False(root.TryGetProperty("rpc-password", out _));
        Assert.False(root.TryGetProperty("rpc-username", out _));
        Assert.False(root.GetProperty("rpc-authentication-required").GetBoolean());
        Assert.True(root.GetProperty("rpc-whitelist-enabled").GetBoolean());
        Assert.Equal("127.0.0.1", root.GetProperty("rpc-bind-address").GetString());
        Assert.Null(Load("transmission").CredentialsUsername);
    }

    [Fact]
    public void Seeded_SABnzbd_adds_no_password_or_host_whitelist_and_no_protection_is_turned_off()
    {
        var ini = Seeded("sabnzbd");

        Assert.DoesNotContain("username", ini);
        Assert.DoesNotContain("password", ini);
        Assert.DoesNotContain("host_whitelist", ini);
        Assert.Contains("host = 127.0.0.1", ini);
        Assert.Null(Load("sabnzbd").CredentialsUsername);
    }

    [Fact]
    public void No_seeded_config_switches_a_protection_off()
    {
        foreach (var kind in new[] { "qbittorrent", "transmission", "sabnzbd", "nzbget", "deluge" })
        {
            var spec = Load(kind);
            if (spec.ConfigFileContent is null)
            {
                continue;
            }

            var text = Seeded(kind);
            Assert.DoesNotContain("LocalHostAuth=false", text);
            Assert.DoesNotContain("CSRFProtection=false", text);
            Assert.DoesNotContain("HostHeaderValidation=false", text);
            Assert.DoesNotContain("\"rpc-whitelist-enabled\": false", text);
        }
    }

    [Fact]
    public void A_login_is_generated_only_for_qBittorrent_which_cannot_start_without_one()
    {
        foreach (var spec in BundledComponents.Load())
        {
            var seedsAPassword = spec.ConfigFileContent is not null
                && (spec.ConfigFileContent.Contains("{{password}}", StringComparison.Ordinal) || spec.ConfigFileContent.Contains("{{qbittorrentPbkdf2}}", StringComparison.Ordinal));
            if (seedsAPassword) { Assert.False(string.IsNullOrWhiteSpace(spec.CredentialsUsername), spec.Kind); }
        }

        // qBittorrent 5.x will not start its Web UI without one. Every other app keeps the login it ships with (#1061),
        // and Deluno connects with each app's own default.
        Assert.False(string.IsNullOrWhiteSpace(Load("qbittorrent").CredentialsUsername));
        foreach (var kind in new[] { "transmission", "sabnzbd", "deluge", "nzbget" })
        {
            Assert.True(string.IsNullOrWhiteSpace(Load(kind).CredentialsUsername), kind);
        }

        foreach (var spec in BundledComponents.Load().Where(s => s.Kind != "qbittorrent"))
        {
            var seed = (spec.ConfigFileContent ?? string.Empty) + string.Join(' ', spec.ConfigReplacements?.Values ?? Enumerable.Empty<string>());
            Assert.DoesNotContain("{{username}}", seed, StringComparison.Ordinal);
            Assert.DoesNotContain("{{password}}", seed, StringComparison.Ordinal);
            Assert.DoesNotContain("Pwd", seed, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("pwd_", seed, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_signature_check_with_no_named_publisher_is_refused_rather_than_accepting_any_trusted_signer()
    {
        Assert.Throws<ArgumentException>(() => ElevatedInstallScript.Build(@"C:\dl\a.exe", "ABCD", "/S", null, null, requireSignature: true, asExe: true));
        Assert.Throws<ArgumentException>(() => ElevatedInstallScript.Build(@"C:\dl\a.exe", "ABCD", "/S", null, "  ", requireSignature: true, asExe: true));
    }

    [Fact]
    public void The_elevated_launch_uses_the_full_path_to_Windows_PowerShell_and_msiexec()
    {
        Assert.True(Path.IsPathFullyQualified(ElevatedInstallScript.PowerShellPath));
        Assert.EndsWith(Path.Combine("WindowsPowerShell", "v1.0", "powershell.exe"), ElevatedInstallScript.PowerShellPath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Environment.SystemDirectory, ElevatedInstallScript.PowerShellPath, StringComparison.OrdinalIgnoreCase);
        var script = ElevatedInstallScript.Build(@"C:\dl\a.msi", "ABCD", "/qn", null);
        Assert.Contains(@"(Join-Path $env:SystemRoot 'System32\msiexec.exe')", script);
        Assert.DoesNotContain("-FilePath 'msiexec.exe'", script);
    }

    [Theory]
    [InlineData("CN=SignPath Foundation, O=SignPath Foundation, L=Lewes, S=Delaware, C=US", true)]
    [InlineData("O=\"SignPath Foundation\", CN=Some Product", true)]
    [InlineData("CN=signpath foundation", true)]
    [InlineData("CN=\"Evil, O=SignPath Foundation\", O=Evil, C=US", false)]
    [InlineData("CN=\"Evil, CN=SignPath Foundation\"", false)]
    [InlineData("CN=Not SignPath Foundation, O=Evil", false)]
    [InlineData("CN=SignPath Foundation Extra, O=Evil", false)]
    [InlineData("OU=SignPath Foundation, CN=Evil", false)]
    // A CN holding a line break could start a line that reads as an O= part of its own.
    [InlineData("CN=\"Evil\nO=SignPath Foundation\", O=Evil", false)]
    [InlineData("CN=\"Evil\r\nCN=SignPath Foundation\"", false)]
    public void The_elevated_publisher_check_matches_a_whole_CN_or_O_and_never_text_inside_a_quoted_value(string subject, bool expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var script = ElevatedInstallScript.PublisherFunction + Environment.NewLine
            + $"$c = [pscustomobject]@{{ SubjectName = New-Object System.Security.Cryptography.X509Certificates.X500DistinguishedName('{subject.Replace("'", "''")}') }}" + Environment.NewLine
            + "if (Test-Publisher $c 'SignPath Foundation') { exit 0 } else { exit 7 }";

        Assert.Equal(expected ? 0 : 7, RunPowerShell(script));
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
    public void Every_manifest_component_and_prerequisite_has_a_hash_or_a_named_publisher_and_only_Weir_is_run_without_elevation()
    {
        foreach (var spec in BundledComponents.Load())
        {
            var hashed = !string.IsNullOrWhiteSpace(spec.Sha256) || !string.IsNullOrWhiteSpace(spec.GithubChecksumAsset)
                || !string.IsNullOrWhiteSpace(spec.GithubChecksumAssetPattern) || !string.IsNullOrWhiteSpace(spec.ChecksumSuffix);
            Assert.True(hashed || (spec.MustBeSigned && !string.IsNullOrWhiteSpace(spec.ExpectedPublisher)), spec.Kind + " has no hash and no named publisher");
            // A signature rule with no hash beside it must always name who signs it.
            if (spec.MustBeSigned && !hashed)
            {
                Assert.False(string.IsNullOrWhiteSpace(spec.ExpectedPublisher), spec.Kind);
            }

            if (spec.Prerequisite is { } prerequisite)
            {
                Assert.True(prerequisite.MustBeSigned && !string.IsNullOrWhiteSpace(prerequisite.ExpectedPublisher), spec.Kind + "'s prerequisite has no named publisher");
            }

            Assert.Equal(spec.Kind != "weir", spec.RunsElevated);
        }
    }

    [Fact]
    public void Every_component_has_a_pinned_or_vendor_published_hash_or_a_signature_rule_its_real_installer_meets()
    {
        // Facts checked against the real installers (30 Sep 2026): qBittorrent 5.2.1, Deluge and NZBGet are NotSigned;
        // Transmission 4.1.1 is signed by SignPath Foundation; SABnzbd 5.0.4 is signed by SignPath Foundation; VC++ redist by Microsoft.
        foreach (var spec in BundledComponents.Load())
        {
            var hashed = !string.IsNullOrWhiteSpace(spec.Sha256)
                || !string.IsNullOrWhiteSpace(spec.GithubChecksumAsset)
                || !string.IsNullOrWhiteSpace(spec.GithubChecksumAssetPattern)
                || !string.IsNullOrWhiteSpace(spec.ChecksumSuffix);
            var signedRule = spec.MustBeSigned && !string.IsNullOrWhiteSpace(spec.ExpectedPublisher);
            Assert.True(hashed || signedRule, spec.Kind + " has neither a hash nor a publisher rule");
        }

        foreach (var unsigned in new[] { "qbittorrent", "deluge", "nzbget" })
        {
            Assert.False(Load(unsigned).MustBeSigned, unsigned + "'s real installer is not signed, so requiring a signature would block it");
        }

        Assert.Equal("SignPath Foundation", Load("transmission").ExpectedPublisher);
        Assert.True(Load("transmission").MustBeSigned);
        Assert.False(string.IsNullOrWhiteSpace(Load("sabnzbd").Sha256));
        Assert.False(string.IsNullOrWhiteSpace(Load("deluge").ChecksumSuffix));
        Assert.False(string.IsNullOrWhiteSpace(Load("nzbget").GithubChecksumAssetPattern));
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
        Assert.Contains("ProgramFiles", script);
        Assert.Contains("Get-AuthenticodeSignature -LiteralPath $copy", script);
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
    public void The_elevated_script_uses_a_folder_no_user_can_be_the_parent_of_and_checks_it_for_links()
    {
        var script = ElevatedInstallScript.Build("C:\\dl\\a.msi", "ABCD", "/qn", null);

        Assert.Contains("$parent = $env:ProgramFiles", script);
        Assert.DoesNotContain("ProgramData", script);
        Assert.Contains("@($parent, $root)", script);
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
        Directory.CreateDirectory(programData);
        Directory.CreateDirectory(elsewhere);
        var junction = Path.Combine(programData, "Deluno installer");
        try
        {
            RunPowerShell($"New-Item -ItemType Junction -Path '{junction}' -Target '{elsewhere}' | Out-Null");

            var download = WriteTempFile(new byte[20], ".msi");
            var script = ElevatedInstallScript.Build(download, "ABCD", "/qn", null, baseFolder: programData);
            var exit = RunPowerShell(script);

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

    private static int RunPowerShell(string script)
    {
        var info = new System.Diagnostics.ProcessStartInfo("powershell.exe", ElevatedInstallScript.PowerShellArguments(script))
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = System.Diagnostics.Process.Start(info)!;
        process.WaitForExit(60000);
        return process.ExitCode;
    }

    [Fact]
    public void A_signature_accepted_download_is_checked_again_on_the_locked_copy_before_it_runs()
    {
        var script = ElevatedInstallScript.Build("C:\\dl\\a.msi", "ABCD", "/qn", null, "SignPath Foundation", requireSignature: true);

        int At(string text) => script.IndexOf(text, StringComparison.Ordinal);
        Assert.True(At("Get-FileHash") < At("Get-AuthenticodeSignature -LiteralPath $copy"));
        Assert.True(At("Get-AuthenticodeSignature") < At("msiexec.exe"));
        Assert.Contains("$sig.Status -ne 'Valid'", script);
        Assert.Contains("Test-Publisher $sig.SignerCertificate 'SignPath Foundation'", script);
        Assert.Contains($"$code = {ElevatedInstallScript.SignatureExitCode}", script);
        Assert.DoesNotContain("Get-AuthenticodeSignature", ElevatedInstallScript.Build("C:\\dl\\a.msi", "ABCD", "/qn", null));
    }

    [Fact]
    public async Task A_signature_failure_inside_the_locked_folder_is_reported_as_a_refused_installer()
    {
        var download = WriteTempFile(new byte[20], ".msi");
        var spec = Load("transmission") with { Sha256 = null, MustBeSigned = true, MinimumBytes = 1, Prerequisite = null, PostInstallCommand = null, ReadinessUrl = null };
        var runner = new FixedExitRunner(ElevatedInstallScript.SignatureExitCode);
        var service = new ComponentInstallService(
            new FakePrograms(), new FakePorts(), new FixedDownloader(download), new PublisherOnlyVerifier(spec.ExpectedPublisher!), runner,
            configWriter: new FakeConfigWriter(true), credentialStore: new RecordingCredentialStore(), startedComponents: new FakeRegistry());

        var result = await service.InstallAsync(spec, CancellationToken.None);

        Assert.Equal(ComponentInstallOutcome.VerificationFailed, result.Outcome);
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
