namespace Deluno.Platform.Security.Hardening;

/// <summary>
/// Identifies which backend an <see cref="ISecretProtector"/> instance is
/// using for new writes. Used by diagnostics surfaces so a user can see
/// at a glance whether their credentials are being stored with
/// platform-native protection or falling back to the legacy DataProtection
/// path (which writes its master key unencrypted to disk on Linux without
/// extra configuration).
/// </summary>
public enum SecretsBackend
{
    /// <summary>
    /// Windows DPAPI via <c>System.Security.Cryptography.ProtectedData</c>.
    /// Per-user scope; ciphertext is bound to the current user account.
    /// </summary>
    WindowsDpapi,

    /// <summary>
    /// AES-256-GCM with a 32-byte master key sourced from the
    /// <c>DELUNO_MASTER_KEY</c> environment variable (base64) or from a
    /// <c>master.key</c> file under the data root. Targeted at Docker /
    /// headless Linux deployments where no system credential vault is
    /// available.
    /// </summary>
    FileBacked,

    /// <summary>
    /// Legacy ASP.NET DataProtection wrapper. On Linux, the DataProtection
    /// master key is written unencrypted to <c>~/.aspnet/DataProtection-Keys/</c>
    /// (or the configured path) unless additional protection is wired in.
    /// Acceptable on Windows (where DataProtection uses DPAPI under the
    /// hood) but flagged as unhardened elsewhere.
    /// </summary>
    DataProtection,

    /// <summary>
    /// macOS Keychain. Not yet implemented — falls back to
    /// <see cref="FileBacked"/> via the factory.
    /// </summary>
    MacOsKeychain,

    /// <summary>
    /// Linux libsecret via D-Bus Secret Service API. Not yet implemented —
    /// falls back to <see cref="FileBacked"/> via the factory.
    /// </summary>
    LinuxLibsecret,
}
