using Deluno.Connections.Data;
using Deluno.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Integrations.Search.Trackers;

/// <summary>
/// Closes the Cloudflare helper's browser sessions Deluno no longer needs
/// (#749), instead of leaving them to accumulate for as long as the helper
/// keeps running:
///
/// <para>On shutdown, every session is destroyed -- Deluno is not coming back
/// for any of them in this run. On a slower interval while running, any
/// session for a host that is no longer a configured, enabled indexer is
/// destroyed too, which is how a session is cleaned up after the indexer that
/// needed it is removed (an interval rather than an event hook, because the
/// helper lives in a different module than the indexer CRUD endpoints and
/// nothing else here needs to be told about a delete right away).</para>
/// </summary>
public sealed class CloudflareHelperSessionCleanupService(
    ICloudflareHelper cloudflareHelper,
    IConnectionsRepository connections,
    ILogger<CloudflareHelperSessionCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(ReconcileInterval, stoppingToken);
                await ReconcileAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await cloudflareHelper.DestroyAllSessionsAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not close every Cloudflare helper session on shutdown.");
        }

        await base.StopAsync(cancellationToken);
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        try
        {
            var activeHosts = (await connections.ListIndexersAsync(cancellationToken))
                .Where(indexer => indexer.IsEnabled)
                .Select(indexer => Uri.TryCreate(IndexerApiEndpoint.NormalizeBaseUrl(indexer.BaseUrl), UriKind.Absolute, out var uri) ? uri.Host : null)
                .Where(host => host is not null)
                .Select(host => host!)
                .ToArray();

            await cloudflareHelper.ReconcileSessionsAsync(activeHosts, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not reconcile Cloudflare helper sessions against configured indexers.");
        }
    }
}
