using Deluno.Security.Contracts;
using Deluno.Security.Data;

namespace Deluno.Security.Hardening;

/// <summary>
/// The Skip sign-in for local devices setting (#993), read once and kept in
/// memory rather than re-queried on every request the authentication check
/// runs on. <see cref="Invalidate"/> is called right after a save so the very
/// next request already sees the new value, with no restart needed.
/// </summary>
public interface ILocalNetworkAuthSettingsCache
{
    Task<LocalNetworkAuthSettings> GetAsync(CancellationToken cancellationToken);

    void Invalidate();
}

public sealed class LocalNetworkAuthSettingsCache(ILocalNetworkAuthRepository repository) : ILocalNetworkAuthSettingsCache
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private LocalNetworkAuthSettings? cached;

    public async Task<LocalNetworkAuthSettings> GetAsync(CancellationToken cancellationToken)
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
