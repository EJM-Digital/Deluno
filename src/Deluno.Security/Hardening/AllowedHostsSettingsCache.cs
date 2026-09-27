using Deluno.Security.Contracts;
using Deluno.Security.Data;
using Microsoft.Extensions.Configuration;

namespace Deluno.Security.Hardening;

/// <summary>
/// The Allowed Hosts setting, read once and kept in memory rather than
/// re-queried on every single request the enforcement middleware sees
/// (#939) -- everything from a poster image to a SignalR heartbeat runs
/// through it. <see cref="Invalidate"/> is called right after a save so the
/// very next request already sees the new list, with no restart needed.
/// </summary>
public interface IAllowedHostsSettingsCache
{
    Task<AllowedHostsSettings> GetAsync(CancellationToken cancellationToken);

    void Invalidate();
}

public sealed class AllowedHostsSettingsCache(IAllowedHostsRepository repository, IConfiguration configuration) : IAllowedHostsSettingsCache
{
    /// <summary>
    /// The documented recovery path that needs no working web UI (#939): set
    /// the <c>DELUNO_ALLOWED_HOSTS_DISABLE</c> environment variable to
    /// <c>true</c>, or <c>Security:AllowedHosts:ForceDisabled</c> in
    /// <c>appsettings.json</c>, and Deluno answers on any address again
    /// regardless of what is saved -- the same as it always did before this
    /// feature. Nothing here writes to the database, so removing the
    /// override brings the saved list back exactly as it was.
    /// </summary>
    private const string ForceDisabledEnvironmentVariable = "DELUNO_ALLOWED_HOSTS_DISABLE";
    private const string ForceDisabledConfigKey = "Security:AllowedHosts:ForceDisabled";

    private readonly SemaphoreSlim gate = new(1, 1);
    private AllowedHostsSettings? cached;

    public async Task<AllowedHostsSettings> GetAsync(CancellationToken cancellationToken)
    {
        if (string.Equals(Environment.GetEnvironmentVariable(ForceDisabledEnvironmentVariable), "true", StringComparison.OrdinalIgnoreCase) ||
            configuration.GetValue<bool>(ForceDisabledConfigKey))
        {
            return AllowedHostsSettings.Off;
        }

        if (cached is { } value) return value;

        await gate.WaitAsync(cancellationToken);
        try
        {
            cached ??= await repository.GetAsync(cancellationToken);
            return cached;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Invalidate() => cached = null;
}
