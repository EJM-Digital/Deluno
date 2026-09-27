using Deluno.Security.Contracts;

namespace Deluno.Security.Data;

/// <summary>Reads and saves the Allowed Hosts setting (#939).</summary>
public interface IAllowedHostsRepository
{
    Task<AllowedHostsSettings> GetAsync(CancellationToken cancellationToken);

    Task SaveAsync(AllowedHostsSettings settings, CancellationToken cancellationToken);
}
