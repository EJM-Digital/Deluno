namespace Deluno.Security.Hardening;

/// <summary>
/// Describes the secrets backend that was selected at startup, surfaced to
/// the diagnostics endpoint so users (and tests) can see what actually
/// got wired in.
/// </summary>
/// <param name="Backend">The active backend for new writes.</param>
/// <param name="IsHardened">
/// True if this backend protects credentials at rest using platform-native
/// crypto bound to user/machine identity (DPAPI, Keychain, libsecret) OR a
/// user-managed master key (FileBacked). False for the DataProtection
/// fallback, which on non-Windows hosts persists its master key
/// unencrypted unless additional protection is configured.
/// </param>
/// <param name="Source">
/// Where the backend was chosen from: e.g. "auto:Windows",
/// "auto:Linux:DELUNO_MASTER_KEY", "auto:Linux:master.key",
/// "auto:Linux:no-key-fallback", "config:dpapi", "config:filebacked",
/// "config:dataprotection".
/// </param>
/// <param name="Warnings">
/// Human-readable warnings about the selection. Empty when fully hardened.
/// Populated when falling back to DataProtection or running with a missing
/// master key.
/// </param>
public sealed record SecretsBackendInfo(
    SecretsBackend Backend,
    bool IsHardened,
    string Source,
    IReadOnlyList<string> Warnings);
