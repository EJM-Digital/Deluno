using Deluno.Security.Contracts;
using Deluno.Security.Data;

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

public sealed class AllowedHostsSettingsCache(IAllowedHostsRepository repository) : IAllowedHostsSettingsCache
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private AllowedHostsSettings? cached;

    public async Task<AllowedHostsSettings> GetAsync(CancellationToken cancellationToken)
    {
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
