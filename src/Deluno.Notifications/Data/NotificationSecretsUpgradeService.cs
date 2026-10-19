using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Notifications.Data;

/// <summary>
/// #1364: an upgraded install keeps its notification addresses and tokens in clear until this runs. Once at
/// start-up, after the databases have their schema, every connection's address and settings are protected the
/// way indexer and client secrets are. A protector cannot be reached from inside a SQL migration, so the
/// upgrade lives here; running it again changes nothing.
/// </summary>
public sealed class NotificationSecretsUpgradeService(
    INotificationRepository notifications,
    ILogger<NotificationSecretsUpgradeService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var changed = await notifications.ProtectStoredSecretsAsync(cancellationToken);
            if (changed > 0)
            {
                logger.LogInformation("Protected the saved address or tokens of {Count} notification connections.", changed);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not protect the saved notification addresses and tokens; they stay as they were.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
