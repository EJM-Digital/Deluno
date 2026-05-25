namespace Deluno.Platform.Security.Hardening;

/// <summary>
/// DI-friendly accessor for the active <see cref="SecretsBackendInfo"/>.
/// The downloader engine will read this on startup to decide whether
/// it's safe to persist user credentials; an unhardened backend on a
/// non-Windows host should result in the engine refusing to start
/// rather than silently writing secrets to a world-readable file.
/// </summary>
public interface ISecretsBackendInfoProvider
{
    SecretsBackendInfo Info { get; }
}

internal sealed class SecretsBackendInfoProvider(SecretsBackendInfo info) : ISecretsBackendInfoProvider
{
    public SecretsBackendInfo Info { get; } = info;
}
