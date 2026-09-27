using System.Security.Cryptography;
using System.Text;
using Deluno.Connections.Data;
using Deluno.Integrations.DownloadClients;
using Deluno.Platform.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Worker.Services;

/// <summary>
/// #822: pushes the content blocklist (#816) into every enabled qBittorrent
/// client's own "Excluded file names" preference, hourly, only when the
/// blocklist's content has actually changed since the last push. Off by
/// default (#936) via <c>ContentBlocklistSettings.SyncToQbittorrentEnabled</c>,
/// and never turns qBittorrent's own "Excluded file names" toggle on - only
/// what the list itself contains.
/// </summary>
public sealed class QbittorrentBlocklistSyncService(
    ILogger<QbittorrentBlocklistSyncService> logger,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider)
    : BackgroundService
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "qBittorrent content-blocklist sync failed; it will try again on its next hourly pass.");
            }

            try
            {
                await Task.Delay(SyncInterval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Runs one sync pass immediately; public so a test can drive it without waiting on the hourly loop.</summary>
    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var healthRepository = scope.ServiceProvider.GetRequiredService<IDownloadHealthRepository>();
        var connectionsRepository = scope.ServiceProvider.GetRequiredService<IConnectionsRepository>();
        var registry = scope.ServiceProvider.GetRequiredService<IDownloadClientRegistry>();

        var contentBlocklist = await healthRepository.GetContentBlocklistSettingsAsync(cancellationToken);
        if (!contentBlocklist.SyncToQbittorrentEnabled)
        {
            return;
        }

        if (!registry.TryGet("qbittorrent", out var downloadClient) || downloadClient is not IQbittorrentBlocklistSyncClient syncClient)
        {
            return;
        }

        var patterns = contentBlocklist.EffectivePatterns();
        var hash = ComputeHash(patterns);
        var syncStates = await healthRepository.ListQbittorrentBlocklistSyncStatesAsync(cancellationToken);
        var clients = (await connectionsRepository.ListDownloadClientsAsync(cancellationToken))
            .Where(client => client.IsEnabled && string.Equals(client.Protocol, "qbittorrent", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var client in clients)
        {
            // Skip unless the blocklist content changed since the last push
            // that actually took, or the last attempt for this client failed
            // and deserves a retry.
            if (syncStates.TryGetValue(client.Id, out var state) && state.Succeeded && string.Equals(state.BlocklistHash, hash, StringComparison.Ordinal))
            {
                continue;
            }

            var (succeeded, message) = await syncClient.PushExcludedFileNamesAsync(client, patterns, cancellationToken);
            // #822: a plain-words count for the Connections screen, not just a
            // silent success - "3 patterns" answers "did anything happen"
            // without the reader having to open the blocklist settings too.
            var recordedMessage = succeeded
                ? $"Pushed {patterns.Count} {(patterns.Count == 1 ? "pattern" : "patterns")} into qBittorrent's Excluded file names."
                : message;
            await healthRepository.RecordQbittorrentBlocklistSyncAsync(client.Id, hash, succeeded, recordedMessage, cancellationToken);
            if (!succeeded)
            {
                logger.LogWarning("Could not push the content blocklist into {ClientName}'s Excluded file names: {Message}", client.Name, message);
            }
        }
    }

    private static string ComputeHash(IReadOnlyList<string> patterns)
    {
        var canonical = string.Join('\n', patterns.OrderBy(pattern => pattern, StringComparer.Ordinal));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes);
    }
}
