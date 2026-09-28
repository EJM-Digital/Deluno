using Deluno.Security.Contracts;

namespace Deluno.Security.Data;

/// <summary>Reads and saves the Skip sign-in for local devices setting (#993).</summary>
public interface ILocalNetworkAuthRepository
{
    Task<LocalNetworkAuthSettings> GetAsync(CancellationToken cancellationToken);

    Task SaveAsync(LocalNetworkAuthSettings settings, CancellationToken cancellationToken);
}
