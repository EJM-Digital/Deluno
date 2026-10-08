using Deluno.Connections.Usenet;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;

namespace Deluno.Integrations.DownloadClients;

/// <summary>
/// Enters the person's Usenet provider once and writes it to every Usenet client Deluno manages (#1142),
/// and says which of them still have none.
/// </summary>
public sealed class UsenetProviderService(IConnectionsRepository connections, IDownloadClientRegistry registry)
{
    public async Task<IReadOnlyList<UsenetProviderClientStatus>> StatusAsync(CancellationToken cancellationToken)
    {
        var statuses = new List<UsenetProviderClientStatus>();
        foreach (var client in await UsenetClientsAsync(cancellationToken))
        {
            bool? has = null;
            try
            {
                has = registry.TryGet(client.Protocol, out var implementation)
                    ? await implementation.HasUsenetProviderAsync(client, cancellationToken)
                    : null;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
            {
                // Unknown: the client could not be asked.
            }

            statuses.Add(new(client.Id, client.Name, has == true, has is not null));
        }

        return statuses;
    }

    /// <summary>Writes the provider to each enabled Usenet client. The password is used for this call only and never stored by Deluno.</summary>
    public async Task<IReadOnlyList<UsenetProviderClientResult>> SaveAsync(UsenetProviderRequest request, CancellationToken cancellationToken)
    {
        var results = new List<UsenetProviderClientResult>();
        foreach (var client in await UsenetClientsAsync(cancellationToken))
        {
            if (!registry.TryGet(client.Protocol, out var implementation))
            {
                continue;
            }

            try
            {
                if (await implementation.SetUsenetProviderAsync(client, request, cancellationToken) is { } result)
                {
                    results.Add(result);
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
            {
                results.Add(new(client.Id, client.Name, false, $"Deluno could not reach {client.Name} to give it the provider."));
            }
        }

        return results;
    }

    private async Task<IReadOnlyList<DownloadClientItem>> UsenetClientsAsync(CancellationToken cancellationToken)
        => (await connections.ListDownloadClientsAsync(cancellationToken))
            .Where(client => client.IsEnabled
                && (string.Equals(client.Protocol, "sabnzbd", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(client.Protocol, "nzbget", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
}
