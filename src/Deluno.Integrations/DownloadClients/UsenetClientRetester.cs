using Deluno.Connections;
using Deluno.Connections.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Integrations.DownloadClients;

/// <summary>Runs a Usenet client's own connection test again and stores the result, as the Test button does.</summary>
public interface IUsenetClientRetester
{
    Task RetestAsync(DownloadClientItem client, CancellationToken cancellationToken);
}

/// <summary>
/// The stored-client test the Test button and the background check run, so the row's status, its message and the
/// live screens all move together. Used after the provider is saved: the row used to keep "No Usenet provider set
/// up" until somebody pressed Test (golden path, rc.30.8).
/// </summary>
public sealed class StoredUsenetClientRetester(IServiceProvider services) : IUsenetClientRetester
{
    public Task RetestAsync(DownloadClientItem client, CancellationToken cancellationToken)
        => ConnectionsEndpointRouteBuilderExtensions.RunStoredDownloadClientTestAsync(client, services, automatic: true, cancellationToken);
}
