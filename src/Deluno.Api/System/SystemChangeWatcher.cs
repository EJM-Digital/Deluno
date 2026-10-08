using System.Text.Json;
using Deluno.Api.Monitoring;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Deluno.Api.SystemInfo;

/// <summary>
/// Tells an open System screen when the two things nothing else announces have changed (#1179):
/// the sizes and maintenance dates of Deluno's databases (<c>SystemDatabasesChanged</c>), and the
/// monitoring readings (<c>MonitoringChanged</c>). Only while a screen on the dashboard subject is
/// open it looks every fifteen seconds, and it pushes only when what a screen shows has changed;
/// with nobody watching it does nothing.
/// </summary>
public sealed class SystemChangeWatcher(
    IOptions<StoragePathOptions> storage,
    IMonitoringService monitoring,
    IRealtimeEventPublisher publisher,
    IRealtimeWatchers? watchers,
    TimeProvider timeProvider,
    ILogger<SystemChangeWatcher> logger)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    private string? _databases;
    private string? _monitoring;

    /// <summary>One look: pushes what changed since the last look. The first look only remembers, so opening a screen never starts with a refetch.</summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        if (watchers is null || watchers.Count("dashboard") == 0)
        {
            return;
        }

        var databases = JsonSerializer.Serialize(SystemLogAndDatabasesEndpointRouteBuilderExtensions.DescribeDatabases(storage.Value.DataRoot));
        if (_databases is not null && !string.Equals(_databases, databases, StringComparison.Ordinal))
        {
            await publisher.PublishSystemDatabasesChangedAsync(cancellationToken);
        }

        _databases = databases;

        var snapshot = await monitoring.GetDashboardAsync(cancellationToken);
        // What the screen shows, without the timestamp and the live machine reading, which differ on every look.
        var signature = JsonSerializer.Serialize(new { snapshot.Readiness, snapshot.Storage, snapshot.Services, snapshot.Alerts });
        if (_monitoring is not null && !string.Equals(_monitoring, signature, StringComparison.Ordinal))
        {
            await publisher.PublishMonitoringChangedAsync(cancellationToken);
        }

        _monitoring = signature;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, timeProvider, stoppingToken);
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "A System change check was skipped.");
            }
        }
    }
}
