#pragma warning disable CA1416 // the Windows protector is driven here with its Windows calls replaced, so it runs on any system
using System.Security.Cryptography;
using Deluno.Security;
using Deluno.Security.Hardening;
using Deluno.Security.Hardening.Backends;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Platform.Tests.Security.Hardening;

/// <summary>The choice of where secrets are kept, shown for each system on any system, with the Windows calls replaced.</summary>
public sealed class SecretBackendChoiceOnAnySystemTests
{
    private static SecretProtectorFactory Factory(
        SecretHostFacts host,
        string? backend = null,
        string? keyPath = null)
    {
        var settings = new Dictionary<string, string?>();
        if (backend is not null)
        {
            settings["Storage:SecretsBackend"] = backend;
        }

        return new SecretProtectorFactory(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            new EphemeralDataProtectionProvider(),
            keyPath ?? Path.Combine(Path.GetTempPath(), "deluno-choice-" + Guid.NewGuid().ToString("N"), "master.key"),
            NullLogger<SecretProtectorFactory>.Instance,
            host);
    }

    private static SecretHostFacts Host(
        bool windows = false,
        string tag = "Linux",
        string? envKey = null,
        bool keyFileThere = false)
        => new(windows, tag, () => envKey, _ => keyFileThere, () => new StubProtector());

    [Theory]
    [InlineData(true, false, false, "Linux")]
    [InlineData(false, true, false, "macOS")]
    [InlineData(false, false, true, "Windows")]
    [InlineData(false, false, false, "Unknown")]
    public void The_system_is_named_for_the_log(bool linux, bool mac, bool windows, string expected)
        => Assert.Equal(expected, SecretHostFacts.TagFor(linux, mac, windows));

    [Fact]
    public void This_system_describes_itself()
    {
        var host = SecretHostFacts.ThisSystem;

        Assert.Equal(OperatingSystem.IsWindows(), host.IsWindows);
        Assert.False(string.IsNullOrEmpty(host.OsTag));
        Assert.False(host.FileExists(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
        _ = host.KeyFromEnvironment();
    }

    [Fact]
    public void Automatic_on_Windows_uses_the_windows_protector_and_the_old_one_stays_readable()
    {
        var (protector, info) = Factory(Host(windows: true, tag: "Windows")).Build();

        Assert.Equal(SecretsBackend.WindowsDpapi, info.Backend);
        Assert.True(info.IsHardened);
        Assert.Equal("auto:Windows", info.Source);
        Assert.Empty(info.Warnings);
        Assert.StartsWith("stub:", protector.Protect("p", "secret"));
    }

    [Fact]
    public void Configured_dpapi_on_Windows_uses_the_windows_protector()
    {
        var (_, info) = Factory(Host(windows: true, tag: "Windows"), "DPAPI").Build();

        Assert.Equal(SecretsBackend.WindowsDpapi, info.Backend);
        Assert.Equal("config:dpapi", info.Source);
    }

    [Fact]
    public void Configured_dpapi_away_from_Windows_falls_back_with_a_warning()
    {
        var (_, info) = Factory(Host(), "dpapi").Build();

        Assert.Equal(SecretsBackend.DataProtection, info.Backend);
        Assert.False(info.IsHardened);
        Assert.Equal("config:dpapi:invalid-on-non-windows", info.Source);
        Assert.Contains("requires Windows", info.Warnings[0]);
    }

    [Fact]
    public void Automatic_with_a_key_in_the_environment_uses_the_file_backed_protector_with_that_key()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        var (_, info) = Factory(Host(tag: "Linux", envKey: key)).Build();

        Assert.Equal(SecretsBackend.FileBacked, info.Backend);
        Assert.StartsWith("auto:Linux:" + FileBackedSecretProtector.EnvVarName, info.Source);
    }

    [Fact]
    public void Automatic_with_a_key_file_already_there_uses_it()
    {
        var path = Path.Combine(Path.GetTempPath(), "deluno-choice-" + Guid.NewGuid().ToString("N"), "master.key");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(32));
        try
        {
            var (_, info) = Factory(Host(tag: "macOS", keyFileThere: true), keyPath: path).Build();

            Assert.Equal(SecretsBackend.FileBacked, info.Backend);
            Assert.StartsWith("auto:macOS:master.key", info.Source);
            Assert.DoesNotContain(info.Warnings, warning => warning.Contains("generated"));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Automatic_with_no_key_anywhere_makes_one_and_warns_to_back_it_up()
    {
        var directory = Path.Combine(Path.GetTempPath(), "deluno-choice-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (_, info) = Factory(Host(tag: "Linux"), keyPath: Path.Combine(directory, "master.key")).Build();

            Assert.Equal(SecretsBackend.FileBacked, info.Backend);
            Assert.Contains("no-key-fallback", info.Source);
            Assert.Contains(info.Warnings, warning => warning.Contains("A new AES master key was generated"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Configured_data_protection_is_hardened_only_on_Windows()
    {
        var (_, windows) = Factory(Host(windows: true, tag: "Windows"), "dataprotection").Build();
        var (_, other) = Factory(Host(), "dataprotection").Build();

        Assert.True(windows.IsHardened);
        Assert.Empty(windows.Warnings);
        Assert.False(other.IsHardened);
        Assert.Contains("legacy DataProtection backend", other.Warnings.Single());
        Assert.Equal("config:dataprotection", other.Source);
    }

    [Fact]
    public void An_unknown_setting_falls_back_with_a_warning_naming_it()
    {
        var (_, info) = Factory(Host(), "  Vault ").Build();

        Assert.Equal(SecretsBackend.DataProtection, info.Backend);
        Assert.Equal("config:unknown-fallback", info.Source);
        Assert.Contains("Unknown Storage:SecretsBackend value 'vault'", info.Warnings[0]);
    }

    [Fact]
    public void A_blank_or_automatic_setting_means_automatic()
    {
        Assert.Equal("auto:Windows", Factory(Host(windows: true, tag: "Windows"), "auto").Build().Info.Source);
        Assert.Equal("auto:Windows", Factory(Host(windows: true, tag: "Windows"), "").Build().Info.Source);
    }

    [Fact]
    public void The_windows_protector_makes_a_prefixed_value_the_unprotect_reverses_with_the_purpose_as_entropy()
    {
        var calls = new List<(string Call, DataProtectionScope Scope, string Entropy)>();
        var protector = new WindowsDpapiSecretProtector(
            DataProtectionScope.LocalMachine,
            (plain, entropy, scope) =>
            {
                calls.Add(("protect", scope, System.Text.Encoding.UTF8.GetString(entropy)));
                return [.. plain.Reverse()];
            },
            (cipher, entropy, scope) =>
            {
                calls.Add(("unprotect", scope, System.Text.Encoding.UTF8.GetString(entropy)));
                return [.. cipher.Reverse()];
            });

        var stored = protector.Protect("nzb", "hunter2");

        Assert.StartsWith(WindowsDpapiSecretProtector.Prefix, stored);
        Assert.True(protector.IsProtected(stored));
        Assert.Equal("hunter2", protector.Unprotect("nzb", stored));
        Assert.Equal(
            [("protect", DataProtectionScope.LocalMachine, "Deluno.Platform.Secrets:nzb"), ("unprotect", DataProtectionScope.LocalMachine, "Deluno.Platform.Secrets:nzb")],
            calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void The_windows_protector_refuses_to_protect_nothing(string? plaintext)
        => Assert.Throws<ArgumentException>(() => new WindowsDpapiSecretProtector(DataProtectionScope.CurrentUser, (p, _, _) => p, (c, _, _) => c).Protect("p", plaintext!));

    [Fact]
    public void The_windows_protector_hands_back_nothing_and_plain_values_unchanged()
    {
        var protector = new WindowsDpapiSecretProtector(DataProtectionScope.CurrentUser, (p, _, _) => p, (c, _, _) => c);

        Assert.Null(protector.Unprotect("p", null));
        Assert.Null(protector.Unprotect("p", "  "));
        Assert.Equal("plain", protector.Unprotect("p", "plain"));
        Assert.False(protector.IsProtected(null));
        Assert.False(protector.IsProtected(" "));
        Assert.False(protector.IsProtected("plain"));
    }

    private sealed class StubProtector : ISecretProtector
    {
        public string Protect(string purpose, string plaintext) => "stub:" + plaintext;

        public string? Unprotect(string purpose, string? protectedValue)
            => protectedValue is not null && protectedValue.StartsWith("stub:", StringComparison.Ordinal) ? protectedValue[5..] : protectedValue;

        public bool IsProtected(string? value) => value is not null && value.StartsWith("stub:", StringComparison.Ordinal);
    }
}
