using Deluno.Connections.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;

namespace Deluno.Integrations.DownloadClients;

public static class FolderChainStatuses
{
    public const string Ok = "ok";
    public const string Problem = "problem";
    public const string Unknown = "unknown";
}

/// <summary>One link in a library's folder chain, and whether it meets the next.</summary>
public sealed record FolderChainLink(string Step, string Status, string Message, string? Path = null);

/// <summary>One routed download client: the category Deluno sends, where the client saves it, and whether that is the library's folder.</summary>
public sealed record FolderChainClient(string ClientId, string ClientName, string Protocol, string Category, IReadOnlyList<FolderChainLink> Links);

/// <summary>
/// Everything a library's downloads pass through, checked end to end (#616).
///
/// <para>A download client, Deluno and a processor each hold a folder of the
/// chain, set in three different places. Nothing checked that they agreed. On
/// the rig, Transmission saved TV to a folder Weir never watched, and nothing
/// on screen said so. This reads each client's real category folder from the
/// client itself and sets it beside the library's folders. It covers only the
/// links the setup has: without a processor, the chain is client, downloads,
/// library.</para>
/// </summary>
public sealed record FolderChainReport(
    string LibraryId,
    string LibraryName,
    string MediaType,
    bool UsesProcessor,
    string? ProcessorName,
    string? DownloadsPath,
    string? ReadyPath,
    string RootPath,
    IReadOnlyList<FolderChainClient> Clients,
    IReadOnlyList<FolderChainLink> LibraryLinks)
{
    public bool AllMatch => Clients.SelectMany(client => client.Links).Concat(LibraryLinks).All(link => link.Status == FolderChainStatuses.Ok);
}

public interface IFolderChainService
{
    Task<FolderChainReport?> CheckAsync(string libraryId, CancellationToken cancellationToken);
}

public sealed class FolderChainService(
    ILibrariesRepository libraries,
    IConnectionsRepository connections,
    IDownloadClientRegistry registry) : IFolderChainService
{
    public async Task<FolderChainReport?> CheckAsync(string libraryId, CancellationToken cancellationToken)
    {
        var library = (await libraries.ListLibrariesAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Id, libraryId, StringComparison.OrdinalIgnoreCase));
        if (library is null)
        {
            return null;
        }

        var routing = await libraries.GetLibraryRoutingAsync(library.Id, cancellationToken);
        var clients = await connections.ListDownloadClientsAsync(cancellationToken);
        var mappings = await connections.ListDownloadClientPathMappingsAsync(null, cancellationToken);
        var usesProcessor = string.Equals(library.ImportWorkflow, "refine-before-import", StringComparison.OrdinalIgnoreCase);
        var tv = string.Equals(library.MediaType, "tv", StringComparison.OrdinalIgnoreCase);
        var kind = tv ? "TV" : "movies";

        var chains = new List<FolderChainClient>();
        foreach (var link in routing?.DownloadClients ?? [])
        {
            var client = clients.FirstOrDefault(item => string.Equals(item.Id, link.DownloadClientId, StringComparison.OrdinalIgnoreCase));
            if (client is null)
            {
                continue;
            }

            var category = !string.IsNullOrWhiteSpace(link.Category) ? link.Category.Trim() : (tv ? client.TvCategory : client.MoviesCategory) ?? string.Empty;
            var links = new List<FolderChainLink>();
            if (!registry.TryGet(client.Protocol, out var implementation))
            {
                links.Add(new("category", FolderChainStatuses.Unknown, $"Deluno cannot read {client.Protocol} settings."));
                chains.Add(new(client.Id, client.Name, client.Protocol, category, links));
                continue;
            }

            var check = await implementation.CheckCategoryAsync(client, category, cancellationToken);
            links.Add(new(
                "category",
                check.Status switch
                {
                    DownloadClientCategoryStatuses.Ready => FolderChainStatuses.Ok,
                    DownloadClientCategoryStatuses.Unreachable => FolderChainStatuses.Unknown,
                    _ => FolderChainStatuses.Problem
                },
                check.Message));

            // The client's path as this host sees it, through any mapping for a
            // client in Docker or on another machine.
            var clientFolder = DownloadClientTelemetryService.TranslateRemotePath(
                check.SavePath,
                mappings.Where(mapping => string.Equals(mapping.DownloadClientId, client.Id, StringComparison.OrdinalIgnoreCase)));
            links.Add(FolderLink(client.Name, category, clientFolder, library, kind, usesProcessor));
            if (ContentLayoutLink(client, category) is { } layoutLink)
            {
                links.Add(layoutLink);
            }

            chains.Add(new(client.Id, client.Name, client.Protocol, category, links));
        }

        return new FolderChainReport(
            library.Id,
            library.Name,
            library.MediaType,
            usesProcessor,
            usesProcessor ? library.ProcessorName : null,
            library.DownloadsPath,
            usesProcessor ? library.ProcessorOutputPath : null,
            library.RootPath,
            chains,
            LibraryLinks(library, usesProcessor, kind, chains.Count));
    }

    /// <summary>
    /// #999: qBittorrent's "no subfolder" content layout drops a multi-file
    /// download's files straight into the category folder. Deluno finds a
    /// download by its own folder, so a season pack's files would sit loose
    /// beside every other download's. Only that choice earns a link; the
    /// others keep each download in its own place.
    /// </summary>
    public static FolderChainLink? ContentLayoutLink(Deluno.Connections.Contracts.DownloadClientItem client, string category)
        => string.Equals(client.Protocol, "qbittorrent", StringComparison.OrdinalIgnoreCase)
           && Deluno.Connections.Contracts.ContentLayouts.Normalize(client.ContentLayout) == Deluno.Connections.Contracts.ContentLayouts.NoSubfolder
            ? new("layout", FolderChainStatuses.Problem,
                $"{client.Name} is set to put files straight into the {category} folder with no subfolder. A download with several files, such as a season pack, will land loose beside every other download, and Deluno cannot tell them apart. Choose \"Keep the torrent's own layout\" or \"Always create a subfolder\" in {client.Name}'s settings.")
            : null;

    private static FolderChainLink FolderLink(string clientName, string category, string? clientFolder, LibraryItem library, string kind, bool usesProcessor)
    {
        var watcher = usesProcessor ? $"the {library.Name} library and {library.ProcessorName ?? "its processor"}" : $"the {library.Name} library";
        if (string.IsNullOrWhiteSpace(clientFolder))
        {
            return new("folder", FolderChainStatuses.Unknown, $"{clientName} did not say where it saves {category}.");
        }

        if (string.IsNullOrWhiteSpace(library.DownloadsPath))
        {
            return new("folder", FolderChainStatuses.Unknown,
                $"{clientName} saves {kind} to {clientFolder}. The {library.Name} library has no downloads folder set, so Deluno cannot check it is the one {watcher} expect.",
                clientFolder);
        }

        return SamePath(clientFolder, library.DownloadsPath)
            ? new("folder", FolderChainStatuses.Ok, $"{clientName} saves {kind} to {clientFolder}, the folder {watcher} expect.", clientFolder)
            : new("folder", FolderChainStatuses.Problem,
                $"{clientName} saves {kind} to {clientFolder}, but {watcher} expect {library.DownloadsPath}. Downloads from {clientName} will not be picked up. Change the {category} folder in {clientName}, or the {library.Name} library's downloads folder.",
                clientFolder);
    }

    private static IReadOnlyList<FolderChainLink> LibraryLinks(LibraryItem library, bool usesProcessor, string kind, int clientCount)
    {
        var links = new List<FolderChainLink>();
        if (clientCount == 0)
        {
            links.Add(new("clients", FolderChainStatuses.Problem, $"No download client is routed to the {library.Name} library, so nothing it finds can be downloaded."));
        }

        links.Add(string.IsNullOrWhiteSpace(library.DownloadsPath)
            ? new("downloads", FolderChainStatuses.Unknown, $"The {library.Name} library has no downloads folder set: the folder its download clients finish {kind} into.")
            : FolderExists("downloads", library.DownloadsPath, $"{kind} finish downloading into"));

        if (usesProcessor)
        {
            var processor = library.ProcessorName ?? "The processor";
            links.Add(string.IsNullOrWhiteSpace(library.ProcessorOutputPath)
                ? new("ready", FolderChainStatuses.Problem, $"{processor} has no ready folder set for the {library.Name} library, so Deluno has nowhere to import its files from.")
                : FolderExists("ready", library.ProcessorOutputPath, $"{processor} leaves finished files in"));
        }

        links.Add(FolderExists("library", library.RootPath, $"the {library.Name} library lives in"));
        return links;
    }

    private static FolderChainLink FolderExists(string step, string path, string role)
    {
        if (!ClientPaths.IsFullClientPath(path))
        {
            return new(step, FolderChainStatuses.Problem, $"{path} is not a full path, so it cannot be the folder {role}. Write it as a full path, such as C:\\Downloads\\Completed or \\\\nas\\media.", path);
        }

        return Directory.Exists(path)
            ? new(step, FolderChainStatuses.Ok, $"{path} is the folder {role}, and Deluno can reach it.", path)
            : new(step, FolderChainStatuses.Problem, $"{path} is the folder {role}, but Deluno cannot reach it from here.", path);
    }

    internal static bool SamePath(string left, string right)
        => string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) => path.Trim().Replace('\\', '/').TrimEnd('/');
}
