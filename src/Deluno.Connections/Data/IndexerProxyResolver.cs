using Deluno.Connections.Contracts;

namespace Deluno.Connections.Data;

/// <summary>
/// An indexer's own assigned proxy (#748), read the one place every caller
/// that needs to route that indexer's requests reads it from, rather than
/// each repeating the same enabled/id match (search, RSS, caps and grab all
/// need this the same way -- #976).
/// </summary>
public static class IndexerProxyResolver
{
    public static async Task<ProxyItem?> ResolveAsync(IndexerItem indexer, IConnectionsRepository connections, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(indexer.ProxyId))
        {
            return null;
        }

        var proxies = await connections.ListProxiesAsync(cancellationToken);
        return proxies.FirstOrDefault(item => item.IsEnabled && string.Equals(item.Id, indexer.ProxyId, StringComparison.OrdinalIgnoreCase));
    }
}
