using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>
/// Every remote path mapping's local side, reachable and readable, and each
/// download client's reported save path translated through those mappings to
/// a local folder that actually exists (#868).
///
/// <para>A mapping only does its job if the local half of it is real. A
/// typo'd local path, or a share that quietly unmounted, otherwise sits wrong
/// until an import fails days later with nothing pointing back at the
/// mapping that caused it. This is a plain filesystem check, distinct from
/// <see cref="LibraryFolderChainHealthCheck"/> and the per-grab save-path
/// check in <c>DownloadClientGrabService</c>, both of which compare a
/// client's save path against what a specific library expects -- a mapping
/// can be broken on its own even where nothing happens to be routed through
/// it that way.</para>
///
/// <para>Reports "not in use", not a failure, when nothing is configured: no
/// path mappings, and no enabled client with a category whose reported save
/// path needed translating.</para>
/// </summary>
public sealed class RemotePathMappingHealthCheck(
    IConnectionsRepository connections,
    IDownloadClientRegistry registry) : IHealthCheck
{
    private const string NotInUseId = "system:remote-path-mappings";

    public string Category => HealthCheckCategories.RemotePathMapping;

    public async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        var clients = await connections.ListDownloadClientsAsync(cancellationToken);
        var mappings = await connections.ListDownloadClientPathMappingsAsync(null, cancellationToken);
        var enabledMappings = mappings.Where(item => item.IsEnabled).ToArray();

        var outcomes = new List<HealthCheckOutcome>();

        foreach (var mapping in enabledMappings)
        {
            outcomes.Add(MappingOutcome(mapping, clients));
        }

        foreach (var client in clients.Where(item => item.IsEnabled))
        {
            outcomes.AddRange(await SavePathOutcomesAsync(client, enabledMappings, cancellationToken));
        }

        if (outcomes.Count == 0)
        {
            outcomes.Add(HealthCheckOutcome.Ok(
                NotInUseId,
                Category,
                "Remote path mappings",
                "Not in use: no download client path mappings are configured."));
        }

        return outcomes;
    }

    private static HealthCheckOutcome MappingOutcome(DownloadClientPathMappingItem mapping, IReadOnlyList<DownloadClientItem> clients)
    {
        var clientName = clients
            .FirstOrDefault(item => string.Equals(item.Id, mapping.DownloadClientId, StringComparison.OrdinalIgnoreCase))
            ?.Name ?? "a download client";
        var id = $"remote-path-mapping:{mapping.Id}";
        var name = $"{clientName}'s path mapping ({mapping.RemotePath} -> {mapping.LocalPath})";

        if (!ClientPaths.IsFullClientPath(mapping.LocalPath))
        {
            return HealthCheckOutcome.Problem(
                id, HealthCheckCategories.RemotePathMapping, name,
                $"{mapping.LocalPath} is not a full path.",
                cause: $"{clientName}'s path mapping from {mapping.RemotePath} needs a local side that is a full path Deluno can open directly.",
                fix: $"Edit {clientName}'s mapping for {mapping.RemotePath} and give it a full local path, such as C:\\Downloads\\Complete or \\\\nas\\downloads.");
        }

        return ReadFolder(mapping.LocalPath) switch
        {
            FolderReadability.Ok => HealthCheckOutcome.Ok(
                id, HealthCheckCategories.RemotePathMapping, name, $"{mapping.LocalPath} exists and Deluno can read it."),
            FolderReadability.Missing => HealthCheckOutcome.Problem(
                id, HealthCheckCategories.RemotePathMapping, name,
                $"{mapping.LocalPath} does not exist.",
                cause: $"This mapping translates {clientName}'s {mapping.RemotePath} to {mapping.LocalPath}, but that folder is not there -- a drive or share may be unmounted, or the path may no longer be correct.",
                fix: $"Reconnect the drive or share, or correct the local path on {clientName}'s mapping for {mapping.RemotePath}."),
            _ => HealthCheckOutcome.Problem(
                id, HealthCheckCategories.RemotePathMapping, name,
                $"{mapping.LocalPath} exists but Deluno cannot read it.",
                cause: $"{mapping.LocalPath} answers but refuses to be listed -- often a permissions problem.",
                fix: $"Fix permissions on {mapping.LocalPath} so Deluno can read it for {clientName}.")
        };
    }

    private async Task<IReadOnlyList<HealthCheckOutcome>> SavePathOutcomesAsync(
        DownloadClientItem client,
        IReadOnlyList<DownloadClientPathMappingItem> mappings,
        CancellationToken cancellationToken)
    {
        if (!registry.TryGet(client.Protocol, out var implementation))
        {
            // An unsupported protocol is already reported by other checks
            // (folder chain, download client health); this one has nothing
            // extra to add.
            return [];
        }

        // A mapping applies to every client at its host, not only the client
        // it was originally added under (#809).
        // A client Deluno gives a folder to on every grab (Transmission, Deluge; #1073) has no category
        // folder that matters: the folder Deluno sends is the library downloads folder, which the folder
        // chain check already verifies. Inventing "<download folder>/<category>" here reported a folder
        // Deluno never uses as missing on a clean install (#1060).
        if (implementation.SetsSaveFolderPerGrab)
        {
            return [];
        }

        var clientHost = string.IsNullOrWhiteSpace(client.Host) ? null : client.Host.Trim().ToLowerInvariant();
        var clientMappings = mappings
            .Where(item =>
                string.Equals(item.DownloadClientId, client.Id, StringComparison.OrdinalIgnoreCase) ||
                (clientHost is not null && item.Host is not null && string.Equals(item.Host, clientHost, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        var categories = new[] { client.MoviesCategory, client.TvCategory }
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Select(category => category!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var outcomes = new List<HealthCheckOutcome?>(categories.Length);
        foreach (var category in categories)
        {
            outcomes.Add(await SavePathOutcomeAsync(client, implementation, category, clientMappings, cancellationToken));
        }

        return outcomes.Where(outcome => outcome is not null).Select(outcome => outcome!).ToArray();
    }

    private static async Task<HealthCheckOutcome?> SavePathOutcomeAsync(
        DownloadClientItem client,
        IDownloadClient implementation,
        string category,
        IReadOnlyList<DownloadClientPathMappingItem> clientMappings,
        CancellationToken cancellationToken)
    {
        var id = $"remote-path-mapping:save-path:{client.Id}:{category}";
        var name = $"{client.Name}'s save path for {category}";

        DownloadClientCategoryCheckResult check;
        try
        {
            check = await implementation.CheckCategoryAsync(client, category, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException)
        {
            return HealthCheckOutcome.Unknown(
                id, HealthCheckCategories.RemotePathMapping, name,
                $"Deluno could not ask {client.Name} where it saves {category}: {exception.Message}");
        }

        if (check.Status == DownloadClientCategoryStatuses.Unreachable)
        {
            // Already reported by the download-client health check itself;
            // nothing new to say about the save path while it cannot answer.
            return null;
        }

        if (string.IsNullOrWhiteSpace(check.SavePath))
        {
            // Not every protocol reports a save path per category -- not
            // itself a problem.
            return null;
        }

        var translated = TranslateRemotePath(check.SavePath, clientMappings);

        if (!ClientPaths.IsFullClientPath(translated))
        {
            return HealthCheckOutcome.Unknown(
                id, HealthCheckCategories.RemotePathMapping, name,
                $"{client.Name} reports saving {category} to {translated}, which Deluno cannot open directly from here. Add a remote path mapping for this client so Deluno can translate it.");
        }

        return ReadFolder(translated) switch
        {
            FolderReadability.Ok => HealthCheckOutcome.Ok(
                id, HealthCheckCategories.RemotePathMapping, name,
                $"{client.Name} saves {category} to {translated}, and Deluno can read it."),
            FolderReadability.Missing => HealthCheckOutcome.Problem(
                id, HealthCheckCategories.RemotePathMapping, name,
                $"{client.Name} saves {category} to {translated}, but that folder does not exist.",
                cause: $"{client.Name}'s reported {category} save path ({translated}) translates to a local folder Deluno cannot find -- a drive or share may be unmounted, or a path mapping may need adding or correcting.",
                fix: $"Reconnect the drive or share, or add/fix a remote path mapping for {client.Name}."),
            _ => HealthCheckOutcome.Problem(
                id, HealthCheckCategories.RemotePathMapping, name,
                $"{client.Name} saves {category} to {translated}, but Deluno cannot read it.",
                cause: $"{translated} answers but refuses to be listed -- often a permissions problem.",
                fix: $"Fix permissions on {translated} so Deluno can read {client.Name}'s {category} downloads.")
        };
    }

    /// <summary>
    /// Translates a path as a download client reports it into the path this
    /// host would use, through any enabled mapping for that client -- the
    /// same rule <c>DownloadClientTelemetryService.TranslateRemotePath</c>
    /// applies, kept as its own small copy here rather than a shared call
    /// across assemblies (that method is internal to
    /// <c>Deluno.Integrations</c>).
    /// </summary>
    private static string TranslateRemotePath(string reportedPath, IReadOnlyList<DownloadClientPathMappingItem> mappings)
    {
        var normalizedReported = NormalizeSourceKey(reportedPath);
        var match = mappings
            .Select(item => new { Mapping = item, Remote = NormalizeSourceKey(item.RemotePath) })
            .Where(item => normalizedReported.Equals(item.Remote, StringComparison.OrdinalIgnoreCase) ||
                           normalizedReported.StartsWith(item.Remote + "/", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Remote.Length)
            .ThenBy(item => item.Mapping.Priority)
            .FirstOrDefault();

        if (match is null)
        {
            return reportedPath;
        }

        var relative = normalizedReported[match.Remote.Length..].TrimStart('/');
        if (relative.Length == 0)
        {
            return match.Mapping.LocalPath;
        }

        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Aggregate(match.Mapping.LocalPath, Path.Combine);
    }

    private static string NormalizeSourceKey(string path) => path.Trim().Replace('\\', '/').TrimEnd('/');

    private static FolderReadability ReadFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return FolderReadability.Missing;
            }

            // Existing is not the same as readable: a mounted share can
            // answer "yes, I am a folder" and still refuse to be listed.
            using var enumerator = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            enumerator.MoveNext();
            return FolderReadability.Ok;
        }
        catch (UnauthorizedAccessException)
        {
            return FolderReadability.Unreadable;
        }
        catch (IOException)
        {
            return FolderReadability.Unreadable;
        }
    }

    private enum FolderReadability
    {
        Ok,
        Missing,
        Unreadable
    }
}
