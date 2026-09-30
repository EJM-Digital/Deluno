using System.Text.Json;
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
/// a processor watching that old folder would stop seeing them. Written on the
/// first start of a build that has this behaviour, as one Activity line and a
/// Health note that can be cleared from System, Health.
/// </summary>
public sealed class DownloadFolderChangeNoticeService(
    IServiceScopeFactory scopes,
    IOptions<StoragePathOptions> storage,
    ILogger<DownloadFolderChangeNoticeService> logger) : BackgroundService
{
    public const string HealthId = "notice:download-folders";

    /// <summary>How many starts the note waits for a client that does not answer before it stops waiting for it.</summary>
    public const int MaxAttempts = 3;

    /// <summary>What earlier starts already covered, so a client is covered once and the note is not repeated.</summary>
    private sealed class NoticeState
    {
        public int Attempts { get; set; }

        public List<string> Covered { get; set; } = [];

        public List<string> Changes { get; set; } = [];
    }

    private static readonly JsonSerializerOptions StateJson = new(JsonSerializerDefaults.Web);

    private static async Task<NoticeState> ReadStateAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<NoticeState>(await File.ReadAllTextAsync(path, cancellationToken), StateJson) ?? new NoticeState()
                : new NoticeState();
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return new NoticeState();
        }
    }

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

    /// <summary>
    /// Does the check. Returns true when a note was written. One client that does not answer
    /// never holds back the note for the others: the clients that answered are covered now, and
    /// the silent ones are asked again on the next start, up to <see cref="MaxAttempts"/> starts,
    /// after which the note stops waiting for them. The marker is written once every client has
    /// answered or the attempts are used up.
    /// </summary>
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

        var statePath = markerPath + ".state";
        var state = await ReadStateAsync(statePath, cancellationToken);
        var newChanges = new List<string>();
        var unreachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var answered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

                if (state.Covered.Contains(client.Id, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var category = ClientSaveFolders.RoutedCategory(client, library, link.Category);
                var clientMappings = mappings.Where(item => string.Equals(item.DownloadClientId, client.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
                DownloadClientCategoryCheckResult? check = null;
                async Task<DownloadClientCategoryCheckResult> CheckAsync()
                    => check ??= await FolderChainService.CheckCategoryBoundedAsync(implementation, client, category, FolderChainService.DefaultStepTimeout, cancellationToken);
                var named = await ClientSaveFolders.ResolveAsync(libraries, client, library.MediaType, category, clientMappings, cancellationToken,
                    async _ => (await CheckAsync()).SavePath);
                if (check is not null && string.Equals(check.Status, DownloadClientCategoryStatuses.Unreachable, StringComparison.OrdinalIgnoreCase))
                {
                    // The client did not answer (still starting after the upgrade reboot): ask again next start.
                    unreachable.Add(client.Id);
                    continue;
                }

                if (named?.ClientFolder is null)
                {
                    continue;
                }

                check = await CheckAsync();
                if (string.Equals(check.Status, DownloadClientCategoryStatuses.Unreachable, StringComparison.OrdinalIgnoreCase))
                {
                    unreachable.Add(client.Id);
                    continue;
                }

                answered.Add(client.Id);
                var old = DownloadClientTelemetryService.TranslateRemotePath(check.SavePath, clientMappings);
                if (!string.IsNullOrWhiteSpace(old) && !FolderChainService.SamePath(old, named.HostFolder))
                {
                    newChanges.Add($"{client.Name} used to save {library.Name} downloads to {old}; they now go to {named.HostFolder}");
                }
            }
        }

        // A client that answered for one library and not for another is asked again.
        foreach (var id in answered.Where(id => !unreachable.Contains(id)))
        {
            state.Covered.Add(id);
        }

        state.Attempts++;
        state.Changes.AddRange(newChanges.Distinct(StringComparer.OrdinalIgnoreCase));
        var wroteNote = newChanges.Count > 0;
        if (wroteNote)
        {
            var list = string.Join("; ", state.Changes.Distinct(StringComparer.OrdinalIgnoreCase).Take(5));
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
        if (unreachable.Count == 0 || state.Attempts >= MaxAttempts)
        {
            await File.WriteAllTextAsync(markerPath, DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }
        }
        else
        {
            await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(state, StateJson), cancellationToken);
        }

        return wroteNote;
    }
}
