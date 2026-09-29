using Deluno.Connections.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Platform.Data;

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
/// <param name="SavedBy"><c>deluno-per-grab</c> when Deluno names the library's downloads folder on each grab (Transmission, Deluge: #1073); <c>client-category</c> when the client's own category folder decides.</param>
/// <param name="SaveFolder">The folder finished downloads land in, as this host writes it. Null when the client did not say.</param>
/// <param name="ClientSaveFolder">The same folder as the client writes it (differs behind a path mapping).</param>
public sealed record FolderChainClient(
    string ClientId,
    string ClientName,
    string Protocol,
    string Category,
    IReadOnlyList<FolderChainLink> Links,
    string SavedBy = FolderChainSavedBy.ClientCategory,
    string? SaveFolder = null,
    string? ClientSaveFolder = null);

public static class FolderChainSavedBy
{
    public const string ClientCategory = "client-category";
    public const string DelunoPerGrab = "deluno-per-grab";
}

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

/// <summary>
/// One place a library's downloads can land, published for a processor (#1073).
/// <c>CategoryKind</c> is <c>category</c> or <c>label</c>. <c>SaveFolder</c> is as Deluno's host
/// writes it, <c>ClientSaveFolder</c> as the client writes it. <c>SavedBy</c> is
/// <c>deluno-per-grab</c> or <c>client-category</c>. <c>MatchesDownloadsPath</c> is null when the
/// client did not say. <c>Status</c> is <c>ok</c>, <c>problem</c> or <c>unknown</c>.
/// </summary>
public sealed record DownloadDestination(
    string DownloadClientId,
    string DownloadClientName,
    string Protocol,
    string Category,
    string CategoryKind,
    string? SaveFolder,
    string? ClientSaveFolder,
    string SavedBy,
    bool? MatchesDownloadsPath,
    string Status,
    string Message);

/// <summary>A library and every download destination Deluno routes it to.</summary>
public sealed record LibraryDownloadDestinations(
    string LibraryId,
    string LibraryName,
    string MediaType,
    string? DownloadsPath,
    string? ProcessorOutputPath,
    string ImportWorkflow,
    IReadOnlyList<DownloadDestination> Destinations,
    ProcessorConnectionDestinations? ProcessorConnection = null);

/// <summary>One path pair of a processor connection. <c>DelunoPath</c> is the path as Deluno sees it; <c>ProcessorPath</c> is the same location as the processor sees it.</summary>
public sealed record ProcessorDestinationPathMapping(string DelunoPath, string ProcessorPath);

/// <summary>The processor connection a library hands its downloads to, and that connection's enabled path mappings, highest priority first (#1073).</summary>
public sealed record ProcessorConnectionDestinations(string Id, string Name, IReadOnlyList<ProcessorDestinationPathMapping> PathMappings);

public interface IFolderChainService
{
    Task<FolderChainReport?> CheckAsync(string libraryId, CancellationToken cancellationToken);

    /// <summary>The download destinations of one library, or of every library when none is named. Null for an unknown library.</summary>
    Task<IReadOnlyList<LibraryDownloadDestinations>?> ListDestinationsAsync(string? libraryId, CancellationToken cancellationToken);
}

public sealed class FolderChainService(
    ILibrariesRepository libraries,
    IConnectionsRepository connections,
    IDownloadClientRegistry registry,
    IProcessorRepository? processors = null) : IFolderChainService
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
            var clientMappings = mappings.Where(mapping => string.Equals(mapping.DownloadClientId, client.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
            // #1073: Transmission and Deluge have no category folders, so Deluno
            // names the library's downloads folder on every grab. Where it does,
            // that folder is the one downloads land in, whatever the client is
            // set to. Where libraries sharing a category disagree it names none,
            // and the client's own folder is checked below as before.
            var perGrab = implementation.SetsSaveFolderPerGrab
                ? await ClientSaveFolders.ResolveAsync(libraries, client, library.MediaType, category, clientMappings, cancellationToken)
                : null;
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
            var clientFolder = perGrab?.HostFolder ?? DownloadClientTelemetryService.TranslateRemotePath(check.SavePath, clientMappings);
            links.Add(perGrab is not null
                ? new("folder", FolderChainStatuses.Ok,
                    $"Deluno sends each {kind} download to {perGrab.ClientFolder} itself, the folder the {library.Name} library expects, whatever folder {client.Name} is set to by default.",
                    perGrab.HostFolder)
                : FolderLink(client.Name, category, clientFolder, library, kind, usesProcessor));
            if (ContentLayoutLink(client, category) is { } layoutLink)
            {
                links.Add(layoutLink);
            }

            chains.Add(new(client.Id, client.Name, client.Protocol, category, links,
                perGrab is not null ? FolderChainSavedBy.DelunoPerGrab : FolderChainSavedBy.ClientCategory,
                clientFolder,
                perGrab?.ClientFolder ?? check.SavePath));
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

    public async Task<IReadOnlyList<LibraryDownloadDestinations>?> ListDestinationsAsync(string? libraryId, CancellationToken cancellationToken)
    {
        var all = await libraries.ListLibrariesAsync(cancellationToken);
        var selected = string.IsNullOrWhiteSpace(libraryId)
            ? all.ToArray()
            : all.Where(item => string.Equals(item.Id, libraryId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (selected.Length == 0 && !string.IsNullOrWhiteSpace(libraryId))
        {
            return null;
        }

        var result = new List<LibraryDownloadDestinations>();
        foreach (var library in selected)
        {
            var report = await CheckAsync(library.Id, cancellationToken);
            if (report is null)
            {
                continue;
            }

            var destinations = report.Clients.Select(client =>
            {
                var status = client.Links.Any(link => link.Status == FolderChainStatuses.Problem) ? FolderChainStatuses.Problem
                    : client.Links.Any(link => link.Status == FolderChainStatuses.Unknown) ? FolderChainStatuses.Unknown
                    : FolderChainStatuses.Ok;
                var message = client.Links.FirstOrDefault(link => link.Status == FolderChainStatuses.Problem)?.Message
                    ?? client.Links.FirstOrDefault(link => link.Step == "folder")?.Message
                    ?? string.Empty;
                bool? matches = string.IsNullOrWhiteSpace(client.SaveFolder) || string.IsNullOrWhiteSpace(library.DownloadsPath)
                    ? null
                    : SamePath(client.SaveFolder, library.DownloadsPath);
                return new DownloadDestination(
                    client.ClientId, client.ClientName, client.Protocol, client.Category,
                    CategoryKind(client.Protocol), client.SaveFolder, client.ClientSaveFolder, client.SavedBy, matches, status, message);
            }).ToArray();
            ProcessorConnectionDestinations? processorConnection = null;
            if (report.UsesProcessor && processors is not null && !string.IsNullOrWhiteSpace(library.ProcessorName)
                && await processors.FindProcessorConnectionByNameAsync(library.ProcessorName, cancellationToken) is { } connection)
            {
                var mappings = await processors.ListProcessorPathMappingsAsync(connection.Id, cancellationToken);
                processorConnection = new(connection.Id, connection.Name,
                    mappings.Where(mapping => mapping.IsEnabled)
                        .Select(mapping => new ProcessorDestinationPathMapping(mapping.DelunoPath, mapping.ProcessorPath)).ToArray());
            }

            result.Add(new LibraryDownloadDestinations(
                library.Id, library.Name, library.MediaType, library.DownloadsPath,
                report.UsesProcessor ? report.ReadyPath : null, library.ImportWorkflow ?? string.Empty, destinations, processorConnection));
        }

        return result;
    }

    /// <summary>What the client calls the tag Deluno routes by: Usenet clients and qBittorrent have categories; the other torrent clients have labels.</summary>
    private static string CategoryKind(string protocol)
        => protocol.ToLowerInvariant() is "sabnzbd" or "nzbget" or "qbittorrent" ? "category" : "label";

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
