using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Deluno.Integrations.DownloadClients;

/// <summary>
/// The one-time note after an upgrade that Transmission and Deluge now save each
/// grab into the library's downloads folder (#1073). An install that already used
/// either client kept their downloads wherever the client itself was set to save;
/// a processor watching that old folder would stop seeing them. Written once, on
/// the first start of a build that has this behaviour, as one Activity line and a
/// Health note that can be cleared from System, Health.
/// </summary>
public sealed class DownloadFolderChangeNoticeService(
    IServiceScopeFactory scopes,
    IOptions<StoragePathOptions> storage,
    ILogger<DownloadFolderChangeNoticeService> logger) : BackgroundService
{
    public const string HealthId = "notice:download-folders";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // After the schema initializers have run.
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
            await using var scope = scopes.CreateAsyncScope();
            var provider = scope.ServiceProvider;
            await RunOnceAsync(
                provider.GetRequiredService<ILibrariesRepository>(),
                provider.GetRequiredService<IConnectionsRepository>(),
                provider.GetRequiredService<IDownloadClientRegistry>(),
                provider.GetRequiredService<IActivityFeedRepository>(),
                provider.GetRequiredService<IHealthCheckRepository>(),
                Path.Combine(Path.GetFullPath(storage.Value.DataRoot), "upgrade-notices", "download-folders-v1.done"),
                stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // No marker was written, so the next start tries again.
            logger.LogWarning(exception, "Could not work out whether the download folder change needs a note.");
        }
    }

    /// <summary>Does the check once. Returns true when a note was written. Writes the marker either way, so it never runs twice.</summary>
    public static async Task<bool> RunOnceAsync(
        ILibrariesRepository libraries,
        IConnectionsRepository connections,
        IDownloadClientRegistry registry,
        IActivityFeedRepository activity,
        IHealthCheckRepository health,
        string markerPath,
        CancellationToken cancellationToken)
    {
        if (File.Exists(markerPath))
        {
            return false;
        }

        var changes = new List<string>();
        var clients = await connections.ListDownloadClientsAsync(cancellationToken);
        var mappings = await connections.ListDownloadClientPathMappingsAsync(null, cancellationToken);
        foreach (var library in await libraries.ListLibrariesAsync(cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(library.DownloadsPath))
            {
                continue;
            }

            var routing = await libraries.GetLibraryRoutingAsync(library.Id, cancellationToken);
            foreach (var link in routing?.DownloadClients ?? [])
            {
                var client = clients.FirstOrDefault(item => string.Equals(item.Id, link.DownloadClientId, StringComparison.OrdinalIgnoreCase));
                if (client is null || !registry.TryGet(client.Protocol, out var implementation) || !implementation.SetsSaveFolderPerGrab)
                {
                    continue;
                }

                var category = ClientSaveFolders.RoutedCategory(client, library, link.Category);
                var clientMappings = mappings.Where(item => string.Equals(item.DownloadClientId, client.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
                var named = await ClientSaveFolders.ResolveAsync(libraries, client, library.MediaType, category, clientMappings, cancellationToken);
                if (named?.ClientFolder is null)
                {
                    continue;
                }

                var check = await FolderChainService.CheckCategoryBoundedAsync(implementation, client, category, FolderChainService.DefaultStepTimeout, cancellationToken);
                var old = DownloadClientTelemetryService.TranslateRemotePath(check.SavePath, clientMappings);
                if (!string.IsNullOrWhiteSpace(old) && !FolderChainService.SamePath(old, named.HostFolder))
                {
                    changes.Add($"{client.Name} used to save {library.Name} downloads to {old}; they now go to {named.HostFolder}");
                }
            }
        }

        if (changes.Count > 0)
        {
            var list = string.Join("; ", changes.Distinct(StringComparer.OrdinalIgnoreCase).Take(5));
            var message = $"Downloads from Transmission and Deluge now go to the library's downloads folder instead of the client's own folder: {list}. "
                + "If a processor watches the old folder, point it at the library's downloads folder.";
            await activity.RecordActivityAsync("downloads.folder-changed", message, null, null, "download-client", null, cancellationToken);
            await health.UpsertResultsAsync(
                [HealthCheckOutcome.Problem(
                    HealthId,
                    HealthCheckCategories.Notice,
                    "Downloads now go to the library's downloads folder",
                    message,
                    cause: "Deluno now names the library's downloads folder on every Transmission and Deluge download.",
                    fix: "Point any processor that watched the old folder at the library's downloads folder, then clear this note.")],
                cancellationToken);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        await File.WriteAllTextAsync(markerPath, DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
        return changes.Count > 0;
    }
}
