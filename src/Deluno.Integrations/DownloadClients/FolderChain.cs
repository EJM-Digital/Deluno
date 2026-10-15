using Deluno.Connections.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Integrations.Processors;
using Deluno.Platform.Contracts;
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
/// <param name="IncompleteFolder">Where the client keeps a download while it is in progress ("where it downloads"), as this host writes it (#1164). Null when the client did not say.</param>
/// <param name="ClientIncompleteFolder">The same folder as the client writes it.</param>
public sealed record FolderChainClient(
    string ClientId,
    string ClientName,
    string Protocol,
    string Category,
    IReadOnlyList<FolderChainLink> Links,
    string SavedBy = FolderChainSavedBy.ClientCategory,
    string? SaveFolder = null,
    string? ClientSaveFolder = null,
    string? IncompleteFolder = null,
    string? ClientIncompleteFolder = null);

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
    IProcessorRepository? processors = null,
    TimeSpan? stepTimeout = null,
    IProcessorConnectionService? processorService = null) : IFolderChainService
{
    /// <summary>
    /// #1079: how long one client, or one folder, gets to answer. A client that
    /// never replies, or a network share that has gone away, used to hold the
    /// whole check (and the screen's "Checking...") for as long as the slowest
    /// of them took, one after another. Each now gets this long, all at once,
    /// and the link says it did not answer.
    /// </summary>
    public static readonly TimeSpan DefaultStepTimeout = TimeSpan.FromSeconds(8);

    private TimeSpan Timeout => stepTimeout ?? DefaultStepTimeout;

    /// <summary>How many clients, or folders, are checked at the same time.</summary>
    private const int MaxConcurrentChecks = 4;

    /// <summary>How long a client's answer is kept for the destinations list, so a processor polling it does not hit the client each time (#1073).</summary>
    internal static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, DownloadClientCategoryCheckResult Result)> CategoryCache = new();

    /// <summary>Forgets every kept answer (by tests).</summary>
    internal static void ClearCategoryCache() => CategoryCache.Clear();

    private static string CacheKey(Deluno.Connections.Contracts.DownloadClientItem client, string category)
        => $"{client.Id}|{client.Host}|{client.Port}|{client.EndpointUrl}|{category}".ToLowerInvariant();

    /// <summary>
    /// One client's category answer within the step timeout, for a caller that has no
    /// per-step link to put a timeout in: a client that does not answer comes back as unreachable.
    /// </summary>
    internal static async Task<DownloadClientCategoryCheckResult> CheckCategoryBoundedAsync(
        IDownloadClient implementation,
        Deluno.Connections.Contracts.DownloadClientItem client,
        string category,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            return await WithTimeoutAsync(token => implementation.CheckCategoryAsync(client, category, token), timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return new DownloadClientCategoryCheckResult(
                client.Id, client.Name, category, DownloadClientCategoryStatuses.Unreachable,
                $"{client.Name} did not answer in time, so Deluno could not check its {category} folder.", Supported: true, Found: false);
        }
    }

    public Task<FolderChainReport?> CheckAsync(string libraryId, CancellationToken cancellationToken)
        => CheckCoreAsync(libraryId, useCache: false, cancellationToken);

    private async Task<FolderChainReport?> CheckCoreAsync(string libraryId, bool useCache, CancellationToken cancellationToken)
    {
        var allLibraries = await libraries.ListLibrariesAsync(cancellationToken);
        var library = allLibraries
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

        // #1079: every routed client is asked at once (a few at a time), each with its own step
        // timeout, so one that never answers holds nothing else up. The order of the links is kept.
        using var gate = new SemaphoreSlim(MaxConcurrentChecks);
        async Task<FolderChainClient?> CheckClientAsync(LibraryDownloadClientLinkItem link)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                return await CheckClientCoreAsync(link);
            }
            finally
            {
                gate.Release();
            }
        }

        async Task<FolderChainClient?> CheckClientCoreAsync(LibraryDownloadClientLinkItem link)
        {
            var client = clients.FirstOrDefault(item => string.Equals(item.Id, link.DownloadClientId, StringComparison.OrdinalIgnoreCase));
            if (client is null)
            {
                return null;
            }

            var category = !string.IsNullOrWhiteSpace(link.Category) ? link.Category.Trim() : (tv ? client.TvCategory : client.MoviesCategory) ?? string.Empty;
            var links = new List<FolderChainLink>();
            if (!registry.TryGet(client.Protocol, out var implementation))
            {
                links.Add(new("category", FolderChainStatuses.Unknown, $"Deluno cannot read {client.Protocol} settings."));
                return new(client.Id, client.Name, client.Protocol, category, links);
            }

            DownloadClientCategoryCheckResult check;
            var cacheKey = CacheKey(client, category);
            if (useCache && CategoryCache.TryGetValue(cacheKey, out var kept) && DateTimeOffset.UtcNow - kept.At < CacheFor)
            {
                check = kept.Result;
            }
            else
            {
                try
                {
                    check = await WithTimeoutAsync(token => implementation.CheckCategoryAsync(client, category, token), Timeout, cancellationToken);
                    if (useCache)
                    {
                        CategoryCache[cacheKey] = (DateTimeOffset.UtcNow, check);
                    }
                }
                catch (TimeoutException)
                {
                    // A kept answer from before the client stopped answering is no longer true.
                    CategoryCache.TryRemove(cacheKey, out _);
                    links.Add(new("category", FolderChainStatuses.Unknown, $"{client.Name} did not answer within {(int)Timeout.TotalSeconds} seconds, so its {category} folder could not be checked. Check that {client.Name} is running and reachable, then check again."));
                    return new(client.Id, client.Name, client.Protocol, category, links);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    CategoryCache.TryRemove(cacheKey, out _);
                    links.Add(new("category", FolderChainStatuses.Unknown, $"{client.Name} could not be checked: {ex.Message}"));
                    return new(client.Id, client.Name, client.Protocol, category, links);
                }
            }

            var clientMappings = mappings.Where(mapping => string.Equals(mapping.DownloadClientId, client.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
            // #1073: Transmission and Deluge have no category folders, so Deluno
            // names the library's downloads folder on every grab. Where it does,
            // that folder is the one downloads land in, whatever the client is
            // set to. Where libraries sharing a category disagree it names none,
            // and the client's own folder is checked below as before.
            var perGrab = implementation.SetsSaveFolderPerGrab
                ? await ClientSaveFolders.ResolveAsync(libraries, client, library.MediaType, category, clientMappings, cancellationToken, _ => Task.FromResult<string?>(check.SavePath))
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
            // A client on another machine with no path mapping for the folder cannot be
            // told a path it can resolve, so Deluno names none: the client's own folder
            // applies, and the check says why (#1073).
            var named = perGrab?.ClientFolder is not null ? perGrab : null;
            var clientFolder = named?.HostFolder ?? DownloadClientTelemetryService.TranslateRemotePath(check.SavePath, clientMappings);
            // #1328: a folder a client saves to that is, holds or sits inside any library root puts the library's media in
            // the way of every clean-up that follows a client's remove-with-data. Said before anything is removed.
            if (!string.IsNullOrWhiteSpace(clientFolder) && allLibraries.FirstOrDefault(other => Deluno.Libraries.LibraryFolderLayout.ReachesALibrary([other], clientFolder)) is { } reached)
            {
                links.Add(new("in-library", FolderChainStatuses.Problem,
                    $"{client.Name} saves {kind} to {clientFolder}, which is inside the {reached.Name} library, or holds it. Deluno will never delete files there when {client.Name} removes a download, so they would be left behind, and your media shares a folder with downloads. Point the {category} folder in {client.Name} somewhere outside every library.",
                    clientFolder));
            }

            if (named is not null)
            {
                links.Add(new("folder", FolderChainStatuses.Ok,
                    $"Deluno asks {client.Name} to finish each {kind} download in {named.ClientFolder}, the folder the {library.Name} library expects. While a download is in progress, {client.Name} keeps it in its own incomplete folder, if it has one set.",
                    named.HostFolder));
            }
            else if (perGrab is not null)
            {
                links.Add(new("folder", FolderChainStatuses.Problem, ClientSaveFolders.NeedsMappingMessage, perGrab.HostFolder));
            }
            else
            {
                links.Add(FolderLink(client.Name, category, clientFolder, library, kind, usesProcessor));
            }

            // #1164: where it downloads, apart from where it finishes. Half-finished files in the finished
            // folder are where the processor, the media server and the library look.
            var incompleteHost = DownloadClientTelemetryService.TranslateRemotePath(check.IncompletePath, clientMappings);
            if (check.Status != DownloadClientCategoryStatuses.Unreachable)
            {
                links.Add(IncompleteLink(client, check.IncompletePath, incompleteHost, clientFolder, library.DownloadsPath, kind));
            }

            if (ContentLayoutLink(client, category) is { } layoutLink)
            {
                links.Add(layoutLink);
            }

            return new(client.Id, client.Name, client.Protocol, category, links,
                named is not null ? FolderChainSavedBy.DelunoPerGrab : FolderChainSavedBy.ClientCategory,
                clientFolder,
                named?.ClientFolder ?? check.SavePath,
                string.IsNullOrWhiteSpace(check.IncompletePath) ? null : incompleteHost,
                string.IsNullOrWhiteSpace(check.IncompletePath) ? null : check.IncompletePath);
        }

        var chains = (await Task.WhenAll((routing?.DownloadClients ?? []).Select(CheckClientAsync)))
            .OfType<FolderChainClient>()
            .ToList();

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
            await LibraryLinksAsync(library, usesProcessor, kind, chains.Count, Timeout, cancellationToken, usesProcessor ? await ProcessorFolderLinksAsync(library, cancellationToken) : null));
    }

    /// <summary>
    /// #1144: what the library's processor says about its folders, found in its own library-folders list by the
    /// library's id and translated through the connection's path mappings. Two lines: the folder it watches (its
    /// input) against this library's downloads folder, and the folder it writes to (its output) against the ready
    /// folder set here. An empty list when the processor cannot be asked or reports nothing for this library.
    /// </summary>
    private async Task<IReadOnlyList<FolderChainLink>> ProcessorFolderLinksAsync(LibraryItem library, CancellationToken cancellationToken)
    {
        if (processors is null || processorService is null
            || (string.IsNullOrWhiteSpace(library.ProcessorConnectionId) && string.IsNullOrWhiteSpace(library.ProcessorName))
            || await processors.FindProcessorConnectionAsync(library.ProcessorConnectionId, library.ProcessorName, cancellationToken) is not { } connection)
        {
            return [];
        }

        try
        {
            var support = await WithTimeoutAsync(token => processorService.GetStatusSupportAsync(connection, token), Timeout, cancellationToken);
            if (support.Answer != ProcessorAnswer.Answered)
            {
                return [];
            }

            var folders = support.HasLibraryFolders
                ? await WithTimeoutAsync(token => processorService.GetLibraryFoldersAsync(connection, token), Timeout, cancellationToken)
                : [];
            var entry = ProcessorLibraryFolder.ForLibrary(folders, library.Id);
            var watched = entry?.WatchedFolder ?? support.WatchedPath;
            var output = entry?.OutputFolder;
            var mappings = await processors.ListProcessorPathMappingsAsync(connection.Id, cancellationToken);
            string? Here(string? path) => string.IsNullOrWhiteSpace(path)
                ? null
                : mappings.Count > 0 ? Deluno.Platform.Processing.ProcessorPathMappingTranslator.ToDeluno(path, mappings) : path;

            var links = new List<FolderChainLink>();
            if (Here(watched) is { } watchedHere)
            {
                links.Add(string.IsNullOrWhiteSpace(library.DownloadsPath)
                    ? new("processor", FolderChainStatuses.Unknown, $"{connection.Name} says it watches {watchedHere} for the {library.Name} library, but no downloads folder is set here to compare it with.", watchedHere)
                    : SamePath(watchedHere, library.DownloadsPath)
                        ? new("processor", FolderChainStatuses.Ok, $"{connection.Name} watches {watchedHere} for the {library.Name} library, the downloads folder set here.", watchedHere)
                        : new("processor", FolderChainStatuses.Problem, $"{connection.Name} watches {watchedHere} for the {library.Name} library, but the downloads folder set here is {library.DownloadsPath}. Point {connection.Name} at {library.DownloadsPath}, or change the downloads folder to match.", watchedHere));
            }

            if (Here(output) is { } outputHere)
            {
                links.Add(string.IsNullOrWhiteSpace(library.ProcessorOutputPath)
                    ? new("processor-output", FolderChainStatuses.Unknown, $"{connection.Name} says it writes to {outputHere} for the {library.Name} library, but no ready folder is set here to compare it with.", outputHere)
                    : SamePath(outputHere, library.ProcessorOutputPath)
                        ? new("processor-output", FolderChainStatuses.Ok, $"{connection.Name} writes to {outputHere} for the {library.Name} library, the ready folder set here.", outputHere)
                        : new("processor-output", FolderChainStatuses.Problem, $"{connection.Name} writes to {outputHere} for the {library.Name} library, but the ready folder set here is {library.ProcessorOutputPath}. Point {connection.Name} at {library.ProcessorOutputPath}, or change the ready folder to match.", outputHere));
            }

            return links;
        }
        catch (TimeoutException)
        {
            return [];
        }
    }


    /// <summary>Runs work but stops waiting for it after the timeout, even when the work ignores its token.</summary>
    internal static async Task<T> WithTimeoutAsync<T>(Func<CancellationToken, Task<T>> work, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        var task = Task.Run(() => work(linked.Token), CancellationToken.None);
        var finished = await Task.WhenAny(task, Task.Delay(timeout, cancellationToken));
        if (finished == task)
        {
            try
            {
                return await task;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A client that honours its token (or its own HTTP timeout)
                // ends with a cancellation when the step timeout fires. That is
                // "did not answer", not the caller cancelling: only the
                // caller's own cancellation may propagate.
                throw new TimeoutException();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        _ = task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        throw new TimeoutException();
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

        // Libraries are checked side by side, each client answer is kept for a minute,
        // and each step has the one step timeout (#1073, #1079), so a processor polling
        // this never makes unbounded live calls.
        var reports = await Task.WhenAll(selected.Select(library => CheckCoreAsync(library.Id, useCache: true, cancellationToken)));

        var result = new List<LibraryDownloadDestinations>();
        for (var index = 0; index < selected.Length; index++)
        {
            var library = selected[index];
            var report = reports[index];
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
            if (report.UsesProcessor && processors is not null && (!string.IsNullOrWhiteSpace(library.ProcessorConnectionId) || !string.IsNullOrWhiteSpace(library.ProcessorName))
                && await processors.FindProcessorConnectionAsync(library.ProcessorConnectionId, library.ProcessorName, cancellationToken) is { } connection)
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

    /// <summary>The setting a person changes in each client to move its in-progress folder.</summary>
    private static string IncompleteSetting(string protocol) => protocol.ToLowerInvariant() switch
    {
        "qbittorrent" => "\"Keep incomplete torrents in\" under Downloads",
        "transmission" => "the incomplete folder under Downloads",
        "deluge" => "\"Download to\" under Preferences, Downloads",
        "sabnzbd" => "the Temporary Download Folder under Config, Folders",
        "nzbget" => "InterDir under Settings, Paths",
        _ => "the incomplete downloads folder"
    };

    /// <summary>True when <paramref name="path"/> is <paramref name="parent"/> or sits somewhere inside it.</summary>
    internal static bool IsSameOrInside(string path, string parent)
    {
        var child = Normalize(path);
        var root = Normalize(parent);
        return root.Length > 0 && (string.Equals(child, root, StringComparison.OrdinalIgnoreCase)
            || child.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// #1164: "where it downloads" set beside "where it finishes". It matches the documented layout when
    /// it is a folder of its own, outside the folder the download finishes in and outside the library's
    /// downloads folder. An in-progress folder that equals or sits inside either of those puts
    /// half-finished files where the processor and the library look.
    /// </summary>
    internal static FolderChainLink IncompleteLink(Deluno.Connections.Contracts.DownloadClientItem client, string? clientPath, string? hostPath, string? finishedFolder, string? downloadsPath, string kind)
    {
        if (string.IsNullOrWhiteSpace(clientPath))
        {
            return new("incomplete", FolderChainStatuses.Unknown, $"{client.Name} did not say where it keeps {kind} downloads while they are in progress.");
        }

        var path = string.IsNullOrWhiteSpace(hostPath) ? clientPath : hostPath;
        if (!ClientPaths.IsFullClientPath(path))
        {
            return new("incomplete", FolderChainStatuses.Unknown, $"{client.Name} keeps {kind} downloads in progress in {clientPath}, a folder Deluno cannot compare with the finished folder because it is not a full path.", path);
        }

        foreach (var (folder, name) in new[] { (finishedFolder, "the folder they finish in"), (downloadsPath, "the library's downloads folder") })
        {
            if (string.IsNullOrWhiteSpace(folder) || !IsSameOrInside(path, folder))
            {
                continue;
            }

            var how = SamePath(path, folder) ? "the same as" : "inside";
            return new("incomplete", FolderChainStatuses.Problem,
                $"{client.Name} downloads {kind} into {path}, which is {how} {name} ({folder}). Half-finished files sit where the processor and the library look. Set {IncompleteSetting(client.Protocol)} in {client.Name} to a folder of its own outside it, for example D:\\Incomplete\\{client.Name}.",
                path);
        }

        return new("incomplete", FolderChainStatuses.Ok,
            $"{client.Name} keeps {kind} downloads in progress in {path}, apart from the folder they finish in{(string.IsNullOrWhiteSpace(finishedFolder) ? string.Empty : $" ({finishedFolder})")}.",
            path);
    }

    private static FolderChainLink FolderLink(string clientName, string category, string? clientFolder, LibraryItem library, string kind, bool usesProcessor)
    {
        var watcher = usesProcessor ? $"the {library.Name} library and {library.ProcessorName ?? "its processor"}" : $"the {library.Name} library";
        var expects = usesProcessor ? "expect" : "expects";
        if (string.IsNullOrWhiteSpace(clientFolder))
        {
            return new("folder", FolderChainStatuses.Unknown, $"{clientName} did not say where it saves {category}.");
        }

        if (string.IsNullOrWhiteSpace(library.DownloadsPath))
        {
            return new("folder", FolderChainStatuses.Unknown,
                $"{clientName} saves {kind} to {clientFolder}. The {library.Name} library has no downloads folder set, so Deluno cannot check it is the one {watcher} {expects}.",
                clientFolder);
        }

        return SamePath(clientFolder, library.DownloadsPath)
            ? new("folder", FolderChainStatuses.Ok, $"{clientName} saves {kind} to {clientFolder}, the folder {watcher} {expects}.", clientFolder)
            : new("folder", FolderChainStatuses.Problem,
                $"{clientName} saves {kind} to {clientFolder}, but {watcher} {expects} {library.DownloadsPath}. Downloads from {clientName} will not be picked up. Change the {category} folder in {clientName}, or the {library.Name} library's downloads folder.",
                clientFolder);
    }

    private static async Task<IReadOnlyList<FolderChainLink>> LibraryLinksAsync(LibraryItem library, bool usesProcessor, string kind, int clientCount, TimeSpan timeout, CancellationToken cancellationToken, IReadOnlyList<FolderChainLink>? processorLinks = null)
    {
        var links = new List<FolderChainLink>();
        if (clientCount == 0)
        {
            links.Add(new("clients", FolderChainStatuses.Problem, $"No download client is routed to the {library.Name} library, so nothing it finds can be downloaded."));
        }

        // #1079: the folders are probed together, each with its own timeout, so a dead share
        // costs one timeout for the whole check, not one per folder.
        var downloads = string.IsNullOrWhiteSpace(library.DownloadsPath)
            ? Task.FromResult<FolderChainLink>(new("downloads", FolderChainStatuses.Unknown, $"The {library.Name} library has no downloads folder set: the folder its download clients finish {kind} into."))
            : FolderExistsAsync("downloads", library.DownloadsPath, $"{kind} finish downloading into", timeout, cancellationToken);
        Task<FolderChainLink>? ready = null;
        if (usesProcessor)
        {
            var processor = library.ProcessorName ?? "The processor";
            ready = string.IsNullOrWhiteSpace(library.ProcessorOutputPath)
                ? Task.FromResult<FolderChainLink>(new("ready", FolderChainStatuses.Problem, $"{processor} has no ready folder set for the {library.Name} library, so Deluno has nowhere to import its files from."))
                : FolderExistsAsync("ready", library.ProcessorOutputPath, $"{processor} leaves finished files in", timeout, cancellationToken);
        }

        var root = FolderExistsAsync("library", library.RootPath, $"the {library.Name} library lives in", timeout, cancellationToken);
        links.Add(await downloads);
        if (ready is not null)
        {
            links.Add(await ready);
        }

        if (processorLinks is { Count: > 0 })
        {
            links.AddRange(processorLinks);
        }

        links.Add(await root);
        return links;
    }

    internal static async Task<FolderChainLink> FolderExistsAsync(string step, string path, string role, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!ClientPaths.IsFullClientPath(path))
        {
            return new(step, FolderChainStatuses.Problem, $"{path} is not a full path, so it cannot be the folder {role}. Write it as a full path, such as C:\\Downloads\\Completed or \\\\nas\\media.", path);
        }

        // A share on a machine that is off can hold Directory.Exists for a
        // minute or more; the wait is bounded so the check always finishes.
        bool exists;
        try
        {
            exists = await WithTimeoutAsync(_ => Task.FromResult(Directory.Exists(path)), timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return new(step, FolderChainStatuses.Unknown, $"{path} is the folder {role}, but it did not answer within {(int)timeout.TotalSeconds} seconds. If it is on another machine, check that machine is on and reachable.", path);
        }

        return exists
            ? new(step, FolderChainStatuses.Ok, $"{path} is the folder {role}, and Deluno can reach it.", path)
            : new(step, FolderChainStatuses.Problem, $"{path} is the folder {role}, but Deluno cannot reach it from here.", path);
    }

    internal static bool SamePath(string left, string right)
        => string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) => path.Trim().Replace('\\', '/').TrimEnd('/');
}
