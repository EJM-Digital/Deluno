using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Deluno.Platform.Security.Hardening;
using Deluno.Platform.Security.Hardening.Backends;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Platform.Tests.Security.Hardening;

public class SecretProtectorFactoryTests
{
    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static (string Dir, string MasterKeyPath) FreshKeyPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"deluno-factory-{Guid.NewGuid():N}");
        return (dir, Path.Combine(dir, "master.key"));
    }

    private static SecretProtectorFactory NewFactory(string masterKeyPath, IConfiguration? config = null)
        => new(
            config ?? new ConfigurationBuilder().Build(),
            new EphemeralDataProtectionProvider(),
            masterKeyPath,
            NullLogger<SecretProtectorFactory>.Instance);

    [Fact]
    public void Auto_on_Windows_selects_WindowsDpapi()
    {
        if (!IsWindows) return;

        var (dir, key) = FreshKeyPath();
        try
        {
            Environment.SetEnvironmentVariable(FileBackedSecretProtector.EnvVarName, null);
            var (_, info) = NewFactory(key).Build();

            Assert.Equal(SecretsBackend.WindowsDpapi, info.Backend);
            Assert.True(info.IsHardened);
            Assert.Equal("auto:Windows", info.Source);
            Assert.False(File.Exists(key), "DPAPI path must not create a master.key file.");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Auto_on_non_Windows_with_env_var_selects_FileBacked_via_env()
    {
        if (IsWindows) return; // env-var precedence only matters when DPAPI isn't picked

        Environment.SetEnvironmentVariable(
            FileBackedSecretProtector.EnvVarName,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var (dir, key) = FreshKeyPath();
        try
        {
            var (_, info) = NewFactory(key).Build();

            Assert.Equal(SecretsBackend.FileBacked, info.Backend);
            Assert.True(info.IsHardened);
            Assert.Contains("env:", info.Source);
            Assert.False(File.Exists(key), "Env var path must not create a key file.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(FileBackedSecretProtector.EnvVarName, null);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Config_filebacked_forces_FileBacked_on_any_platform()
    {
        Environment.SetEnvironmentVariable(FileBackedSecretProtector.EnvVarName, null);
        var (dir, key) = FreshKeyPath();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:SecretsBackend"] = "filebacked"
            })
            .Build();
        try
        {
            var (_, info) = NewFactory(key, config).Build();

            Assert.Equal(SecretsBackend.FileBacked, info.Backend);
            Assert.True(info.IsHardened);
            Assert.Equal("config:filebacked:file:" + key + ":created", info.Source);
            Assert.True(File.Exists(key), "First-run with FileBacked must create the master key.");
            // Warning is surfaced about backing up the key.
            Assert.Contains(info.Warnings, w => w.Contains("Back this file up", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Config_dpapi_on_non_Windows_falls_back_to_DataProtection_with_warning()
    {
        if (IsWindows) return;

        var (dir, key) = FreshKeyPath();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:SecretsBackend"] = "dpapi"
            })
            .Build();
        try
        {
            var (_, info) = NewFactory(key, config).Build();

            Assert.Equal(SecretsBackend.DataProtection, info.Backend);
            Assert.False(info.IsHardened);
            Assert.Contains(info.Warnings, w => w.Contains("dpapi requires Windows", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Composite_protector_round_trips_through_factory_output()
    {
        Environment.SetEnvironmentVariable(FileBackedSecretProtector.EnvVarName, null);
        var (dir, key) = FreshKeyPath();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:SecretsBackend"] = "filebacked"
            })
            .Build();
        try
        {
            var (protector, _) = NewFactory(key, config).Build();

            var enc = protector.Protect("p", "round-trip");
            var dec = protector.Unprotect("p", enc);

            Assert.Equal("round-trip", dec);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Legacy_dp_v1_values_still_readable_through_composite()
    {
        Environment.SetEnvironmentVariable(FileBackedSecretProtector.EnvVarName, null);
        var (dir, key) = FreshKeyPath();
        var dpp = new EphemeralDataProtectionProvider();
        var legacyProtector = new Deluno.Platform.Security.DataProtectionSecretProtector(dpp);
        var legacyValue = legacyProtector.Protect("p", "old-value");

        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Storage:SecretsBackend"] = "filebacked"
                })
                .Build();
            var factory = new SecretProtectorFactory(
                config, dpp, key, NullLogger<SecretProtectorFactory>.Instance);
            var (protector, _) = factory.Build();

            var unprotected = protector.Unprotect("p", legacyValue);
            Assert.Equal("old-value", unprotected);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
