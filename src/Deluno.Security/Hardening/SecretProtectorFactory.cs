using Deluno.Security.Hardening.Backends;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Deluno.Security.Hardening;

/// <summary>
/// Selects which <see cref="ISecretProtector"/> backend to wire up at
/// startup. The decision honors explicit configuration first
/// (<c>Storage:SecretsBackend</c>: "auto" | "dpapi" | "filebacked" |
/// "dataprotection") then falls back to OS-based auto-selection:
///
/// - Windows → <see cref="WindowsDpapiSecretProtector"/>
/// - Linux / macOS with <c>DELUNO_MASTER_KEY</c> set → <see cref="FileBackedSecretProtector"/>
/// - Linux / macOS with an existing master.key file → <see cref="FileBackedSecretProtector"/>
/// - Linux / macOS with neither → <see cref="FileBackedSecretProtector"/>
///   with a freshly-generated key (warn the user to back it up)
///
/// The original DataProtection-based protector is always added as a
/// legacy reader for backward compatibility, so existing <c>dp:v1:</c>
/// values keep working through one composite instance.
/// </summary>
public sealed class SecretProtectorFactory
{
    private readonly IConfiguration _configuration;
    private readonly IDataProtectionProvider _dataProtectionProvider;
    private readonly string _masterKeyFilePath;
    private readonly ILogger<SecretProtectorFactory> _logger;

    public SecretProtectorFactory(
        IConfiguration configuration,
        IDataProtectionProvider dataProtectionProvider,
        string masterKeyFilePath,
        ILogger<SecretProtectorFactory> logger)
    {
        _configuration = configuration;
        _dataProtectionProvider = dataProtectionProvider;
        _masterKeyFilePath = masterKeyFilePath;
        _logger = logger;
    }

    public (ISecretProtector Protector, SecretsBackendInfo Info) Build()
    {
        var configured = _configuration["Storage:SecretsBackend"]?.Trim().ToLowerInvariant();
        var (backend, source, configError) = ResolveBackend(configured);

        var legacyDataProtection = new DataProtectionSecretProtector(_dataProtectionProvider);
        var warnings = new List<string>();
        if (configError is not null) warnings.Add(configError);

        ISecretProtector active;
        bool hardened;
        switch (backend)
        {
            case SecretsBackend.WindowsDpapi when OperatingSystem.IsWindows():
                active = new WindowsDpapiSecretProtector();
                hardened = true;
                break;
            case SecretsBackend.WindowsDpapi:
                // ResolveBackend already gates this case to Windows, but
                // satisfy CA1416's flow analysis here too. Falls back
                // gracefully if reached on a non-Windows host.
                active = legacyDataProtection;
                hardened = false;
                warnings.Add("WindowsDpapi backend requested on non-Windows host; falling back to DataProtection.");
                break;

            case SecretsBackend.FileBacked:
                var (key, keySource) = FileBackedSecretProtector.ResolveOrCreateKey(_masterKeyFilePath);
                active = new FileBackedSecretProtector(key);
                hardened = true;
                source = $"{source}:{keySource}";
                if (keySource.EndsWith(":created", StringComparison.Ordinal))
                {
                    warnings.Add(
                        $"A new AES master key was generated at '{_masterKeyFilePath}'. " +
                        "Back this file up — losing it permanently destroys all stored credentials. " +
                        $"For container deployments, set the {FileBackedSecretProtector.EnvVarName} " +
                        "env var to a base64-encoded 32-byte key instead so the key never lands on a volume.");
                }
                break;

            case SecretsBackend.DataProtection:
            default:
                active = legacyDataProtection;
                hardened = OperatingSystem.IsWindows(); // DataProtection on Windows uses DPAPI under the hood; elsewhere it doesn't
                if (!hardened)
                {
                    warnings.Add(
                        "Falling back to the legacy DataProtection backend on a non-Windows host. " +
                        "The DataProtection master key is persisted unencrypted to disk. " +
                        $"Either set {FileBackedSecretProtector.EnvVarName} (base64 32-byte key) " +
                        "or configure Storage:SecretsBackend=filebacked to harden credentials at rest.");
                }
                break;
        }

        var info = new SecretsBackendInfo(backend, hardened, source, warnings);

        // Composite wraps the chosen active backend with the legacy
        // DataProtection reader, so any existing dp:v1: values continue
        // to be readable. (If the active backend IS DataProtection, the
        // composite has no legacy readers — that's still correct.)
        var composite = new CompositeSecretProtector(
            active,
            ReferenceEquals(active, legacyDataProtection)
                ? Array.Empty<ISecretProtector>()
                : new[] { (ISecretProtector)legacyDataProtection });

        LogSelection(info);
        return (composite, info);
    }

    private (SecretsBackend Backend, string Source, string? ConfigError) ResolveBackend(string? configured)
    {
        if (string.IsNullOrEmpty(configured) || configured == "auto")
        {
            if (OperatingSystem.IsWindows())
                return (SecretsBackend.WindowsDpapi, "auto:Windows", null);

            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(FileBackedSecretProtector.EnvVarName)))
                return (SecretsBackend.FileBacked, $"auto:{OsTag()}:{FileBackedSecretProtector.EnvVarName}", null);

            if (File.Exists(_masterKeyFilePath))
                return (SecretsBackend.FileBacked, $"auto:{OsTag()}:master.key", null);

            // No env var, no existing key — auto-pick FileBacked anyway
            // (it will generate a new key with a warning). This is a
            // deliberate choice over silently falling back to the
            // unhardened DataProtection path.
            return (SecretsBackend.FileBacked, $"auto:{OsTag()}:no-key-fallback", null);
        }

        return configured switch
        {
            "dpapi" when OperatingSystem.IsWindows() => (SecretsBackend.WindowsDpapi, "config:dpapi", null),
            "dpapi" => (SecretsBackend.DataProtection, "config:dpapi:invalid-on-non-windows",
                "Storage:SecretsBackend=dpapi requires Windows; falling back to DataProtection."),
            "filebacked" => (SecretsBackend.FileBacked, "config:filebacked", null),
            "dataprotection" => (SecretsBackend.DataProtection, "config:dataprotection", null),
            _ => (SecretsBackend.DataProtection, "config:unknown-fallback",
                $"Unknown Storage:SecretsBackend value '{configured}'; falling back to DataProtection.")
        };
    }

    private static string OsTag()
    {
        if (OperatingSystem.IsLinux()) return "Linux";
        if (OperatingSystem.IsMacOS()) return "macOS";
        if (OperatingSystem.IsWindows()) return "Windows";
        return "Unknown";
    }

    private void LogSelection(SecretsBackendInfo info)
    {
        if (info.IsHardened)
        {
            _logger.LogInformation(
                "Secrets backend selected: {Backend} (source={Source}). Credentials at rest are hardened.",
                info.Backend, info.Source);
        }
        else
        {
            _logger.LogWarning(
                "Secrets backend selected: {Backend} (source={Source}). Credentials are NOT hardened: {Warnings}",
                info.Backend, info.Source, string.Join(" / ", info.Warnings));
        }
        foreach (var w in info.Warnings)
            _logger.LogWarning("Secrets backend warning: {Warning}", w);
    }
}
