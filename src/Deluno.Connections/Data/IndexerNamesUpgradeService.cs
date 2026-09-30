using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Connections.Data;

/// <summary>
/// #1076: an upgraded install's indexers added from a tracker definition keep the name they
/// were typed with until saved, because the definitions are not readable inside a migration.
/// Once at start-up, after the databases have their schema, every indexer takes its automatic name.
/// </summary>
public sealed class IndexerNamesUpgradeService(
    IConnectionsRepository connections,
    ILogger<IndexerNamesUpgradeService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var renamed = await connections.ApplyAutomaticIndexerNamesAsync(cancellationToken);
            if (renamed > 0)
            {
                logger.LogInformation("Gave {Count} indexers their automatic name.", renamed);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not give indexers their automatic names; they keep the names they have.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
