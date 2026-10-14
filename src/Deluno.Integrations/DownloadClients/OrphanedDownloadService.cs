using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Notifications;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging;

namespace Deluno.Integrations.DownloadClients;

/// <summary>
/// Finds, keeps and removes orphaned download folders (#588). Cleanuparr
/// removes orphaned and unlinked download files; Deluno notices them, counts
/// them where a person can see them, and removes one only when asked and only
/// when it can prove it downloaded it.
/// </summary>
public interface IOrphanedDownloadService
{
    /// <summary>
    /// Scans the folders now, from the clients' live answer. What Keep, Remove and
    /// the leftover clean-up decide from: an action never acts on a list that
    /// might be a minute old.
    /// </summary>
    Task<IReadOnlyList<OrphanedDownloadItem>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Scans the folders from the latest client capture (#1081). Slow (it walks
    /// folders), so only the background refresher calls it, never a request.
    /// </summary>
    Task<IReadOnlyList<OrphanedDownloadItem>> ScanAsync(CancellationToken cancellationToken)
        => ListAsync(cancellationToken);

    /// <summary>
    /// What the last background scan found, returned at once (#1081): the
    /// Transfers screen and the sidebar's poll read this and never wait on a
    /// folder walk or a download client.
    /// </summary>
    Task<IReadOnlyList<OrphanedDownloadItem>> ListCachedAsync(CancellationToken cancellationToken)
        => ListAsync(cancellationToken);

    Task<OrphanedDownloadAction> KeepAsync(string path, CancellationToken cancellationToken);

    /// <param name="reason">
    /// Why this counted as a leftover, said in Activity alongside the file
    /// (#932). Null for a person's own click on the Remove button, where the
    /// screen they clicked from already says why.
    /// </param>
    Task<OrphanedDownloadAction> RemoveAsync(string path, CancellationToken cancellationToken, string? reason = null);
}

/// <summary>What happened when a person acted on an orphaned download.</summary>
public sealed record OrphanedDownloadAction(bool Done, string Message);

public sealed class OrphanedDownloadService(
    ILibrariesRepository librariesRepository,
    IPlatformSettingsRepository platformSettingsRepository,
    IDownloadClientTelemetryService telemetryService,
    IJobQueueRepository jobQueueRepository,
    IProcessorRepository processorRepository,
    IKeptDownloadsRepository keptDownloadsRepository,
    IActivityFeedRepository activityFeedRepository,
    TimeProvider timeProvider,
    ILogger<OrphanedDownloadService> logger,
    IOutboundNotificationService? outboundNotificationService = null,
    INotificationService? notificationPreferences = null,
    OrphanedDownloadCache? cache = null,
    LeftoverScanMemory? scanMemory = null,
    IRealtimeEventPublisher? realtimeEventPublisher = null,
    // #1318: an orphaned download leaves through the recycle bin, never off the disk.
    Deluno.Platform.IRecycleBinService? recycleBinService = null)
    : IOrphanedDownloadService
{
    public async Task<IReadOnlyList<OrphanedDownloadItem>> ListAsync(CancellationToken cancellationToken)
        => await ScanFromAsync(await telemetryService.GetSnapshotsForLeftoverScanAsync(cancellationToken), cancellationToken);

    // Awaited, because a wrong list here would show a download the client still
    // holds as an orphan: never the non-blocking read.
    public async Task<IReadOnlyList<OrphanedDownloadItem>> ScanAsync(CancellationToken cancellationToken)
        => await ScanFromAsync(
            await telemetryService.GetFreshSnapshotsForLeftoverScanAsync(DownloadTelemetryFreshness.Relaxed, cancellationToken),
            cancellationToken);

    public async Task<IReadOnlyList<OrphanedDownloadItem>> ListCachedAsync(CancellationToken cancellationToken)
        => cache is null
            ? await ListAsync(cancellationToken)
            : await cache.GetLatestAsync(OrphanedDownloadRefreshService.Interval, cancellationToken);

    private async Task<IReadOnlyList<OrphanedDownloadItem>> ScanFromAsync(
        IReadOnlyList<DownloadClientTelemetrySnapshot> clientSnapshots,
        CancellationToken cancellationToken)
    {
        var found = await ScanCoreAsync(clientSnapshots, cancellationToken);
        // Remembered for the Dashboard, which never walks a folder itself.
        var previous = scanMemory?.Last;
        var bytes = found.Sum(item => item.SizeBytes);
        scanMemory?.Record(found.Count, bytes, timeProvider.GetUtcNow());
        // A leftover folder appearing or going changes what the bell counts; the first reading only sets the baseline.
        if (previous is not null && (previous.Count != found.Count || previous.Bytes != bytes))
        {
            await realtimeEventPublisher.PublishSafelyAsync(publisher => publisher.PublishAttentionChangedAsync("orphans", cancellationToken));
        }

        return found;
    }

    private async Task<IReadOnlyList<OrphanedDownloadItem>> ScanCoreAsync(
        IReadOnlyList<DownloadClientTelemetrySnapshot> clientSnapshots,
        CancellationToken cancellationToken)
    {
        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        // Deluno's own downloads decide which folders are scanned and are
        // claimed by their paths. What another tool -- or a person -- added to
        // a client (#958, #1062) is never listed or scanned for, but its files
        // are still in use: those paths only ever protect them from being
        // swept up.
        var scans = DownloadFolders(libraries, clientSnapshots)
            .Select(folder => new DownloadFolderScan(folder.LibraryId, folder.LibraryName, folder.Path, ReadTopLevel(folder.Path)))
            .ToArray();
        if (scans.All(scan => scan.Entries.Count == 0))
        {
            return [];
        }

        var settings = await platformSettingsRepository.GetAsync(cancellationToken);
        var jobs = await jobQueueRepository.ListAsync(600, cancellationToken);
        var handoffs = await processorRepository.ListProcessorHandoffsAsync(null, 500, cancellationToken);
        // Asked by the names on disk, archived dispatches included, so it does not
        // matter how many newer dispatches there are (#1062).
        var grabbedNames = await jobQueueRepository.FindGrabbedReleaseNamesAsync(
            scans.SelectMany(scan => scan.Entries).SelectMany(OrphanedDownloads.CandidateNames).ToArray(),
            cancellationToken);

        var claimed = new List<string>();
        foreach (var client in clientSnapshots)
        {
            claimed.AddRange(client.Queue.Select(item => item.SourcePath ?? string.Empty));
            claimed.AddRange(client.History.Select(item => item.SourcePath ?? string.Empty));
            claimed.AddRange(client.OutsideSourcePaths);
        }

        claimed.AddRange(jobs
            .Where(job => string.Equals(job.JobType, "filesystem.import.execute", StringComparison.OrdinalIgnoreCase))
            .Select(job => DownloadClientTelemetryService.TryReadImportSourcePath(job.PayloadJson) ?? string.Empty));
        claimed.AddRange(handoffs.SelectMany(handoff => new[] { handoff.SourcePath, handoff.OutputPath ?? string.Empty }));
        // The folders Deluno was configured with are never orphans, whatever
        // they hold: libraries, processor outputs, other libraries' downloads.
        claimed.AddRange(libraries.SelectMany(library => new[] { library.RootPath, library.ProcessorOutputPath ?? string.Empty, library.DownloadsPath ?? string.Empty }));
        claimed.Add(settings.IncompleteDownloadsPath ?? string.Empty);

        var found = OrphanedDownloads.Find(
            scans,
            claimed,
            grabbedNames,
            await keptDownloadsRepository.ListAsync(cancellationToken),
            timeProvider.GetUtcNow(),
            // #1001: the person's skip list and how long an entry must have
            // sat. Untouched settings are no skip list and the 2 hours this
            // always used.
            OrphanedDownloads.ParseExcludePatterns(settings.LeftoverExcludePatterns),
            TimeSpan.FromHours(Math.Max(1, settings.LeftoverMinAgeHours)));

        // Sized only once found: a completed folder can hold hundreds of
        // seeding torrents, and walking every one of them to report none is
        // the expensive way to say nothing.
        return found.Select(item => item with { SizeBytes = SizeOf(item.Path) }).ToArray();
    }

    public async Task<OrphanedDownloadAction> KeepAsync(string path, CancellationToken cancellationToken)
    {
        var item = await FindAsync(path, cancellationToken);
        if (item is null)
        {
            return new OrphanedDownloadAction(false, "That is not an orphaned download any more.");
        }

        await keptDownloadsRepository.KeepAsync(item.Path, cancellationToken);
        cache?.Forget(item.Path);
        await activityFeedRepository.RecordActivityAsync(
            "download.orphan.kept",
            $"{item.Name} was kept in the {item.LibraryName} library's download folder. Deluno will not count it again.",
            null,
            null,
            "library",
            item.LibraryId,
            cancellationToken);
        return new OrphanedDownloadAction(true, $"Kept {item.Name}.");
    }

    public async Task<OrphanedDownloadAction> RemoveAsync(string path, CancellationToken cancellationToken, string? reason = null)
    {
        var item = await FindAsync(path, cancellationToken);
        if (item is null)
        {
            return new OrphanedDownloadAction(false, "That is not an orphaned download any more.");
        }

        // Never inside a library, whatever else is true: a download folder
        // configured inside a library root must not let this reach media.
        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        var key = OrphanedDownloads.Key(item.Path);
        if (libraries.Any(library => !string.IsNullOrWhiteSpace(library.RootPath)
            && key.StartsWith(OrphanedDownloads.Key(library.RootPath) + "/", StringComparison.OrdinalIgnoreCase)))
        {
            return new OrphanedDownloadAction(false, $"{item.Name} is inside a library, so Deluno will not remove it.");
        }

        var owner = libraries.FirstOrDefault(library => string.Equals(library.Id, item.LibraryId, StringComparison.OrdinalIgnoreCase));
        if (recycleBinService is null || owner is null)
        {
            return new OrphanedDownloadAction(false, $"Deluno has no recycle bin to move {item.Name} to, so it was left where it is.");
        }

        if (Directory.Exists(item.Path) || File.Exists(item.Path))
        {
            var stored = Directory.Exists(item.Path)
                ? await recycleBinService.StoreDownloadFolderAsync(owner, item.Path, cancellationToken)
                : await recycleBinService.StoreDownloadFileAsync(owner, item.Path, cancellationToken);
            if (stored is null)
            {
                logger.LogWarning("Could not move orphaned download {Path} to the recycle bin.", item.Path);
                return new OrphanedDownloadAction(false, $"Deluno could not move {item.Name} to the recycle bin, so it was left where it is.");
            }
        }

        cache?.Forget(item.Path);
        var noticeSuffix = reason is { Length: > 0 } ? $", {reason}." : ".";
        var message = $"{item.Name} was moved to the recycle bin from the {item.LibraryName} library's download folder ({FormatSize(item.SizeBytes)}). No client, import or hand-off was using it{noticeSuffix}";
        await activityFeedRepository.RecordActivityAsync(
            "download.orphan.removed",
            message,
            null,
            null,
            "library",
            item.LibraryId,
            cancellationToken);

        // Deluno's own recovery/cleanup actions get an outbound category too
        // (#858), not just an Activity entry. Never lets a misconfigured
        // webhook affect the cleanup itself.
        if (outboundNotificationService is not null)
        {
            var enabled = notificationPreferences is null ||
                (await notificationPreferences.GetPreferencesAsync(cancellationToken)).OrphanedDownloadRemovedEnabled;
            if (enabled)
            {
                await outboundNotificationService.DispatchAsync(
                    "download.orphan.removed",
                    $"Orphaned download removed: {item.Name}",
                    message,
                    null,
                    cancellationToken);
            }
        }

        return new OrphanedDownloadAction(true, $"Removed {item.Name}.");
    }

    private async Task<OrphanedDownloadItem?> FindAsync(string path, CancellationToken cancellationToken)
    {
        var key = OrphanedDownloads.Key(path ?? string.Empty);
        return (await ListAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(OrphanedDownloads.Key(item.Path), key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The folders completed downloads land in: each library's own download
    /// folder when it has one, and every folder the clients report saving into
    /// (the parent of each item they list). Most libraries set no download
    /// folder, so the clients' own paths are how Deluno knows where to look.
    /// A folder inside a library or a processor's output is never scanned:
    /// everything in it would look unclaimed, and it is media.
    /// </summary>
    internal static IReadOnlyList<(string Path, string LibraryId, string LibraryName)> DownloadFolders(
        IReadOnlyList<Deluno.Libraries.Contracts.LibraryItem> libraries,
        IReadOnlyList<DownloadClientTelemetrySnapshot> clientSnapshots)
    {
        var protectedRoots = libraries
            .SelectMany(library => new[] { library.RootPath, library.ProcessorOutputPath ?? string.Empty })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(OrphanedDownloads.Key)
            .ToArray();
        var folders = new Dictionary<string, (string Path, string LibraryId, string LibraryName)>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path, Deluno.Libraries.Contracts.LibraryItem? library)
        {
            if (string.IsNullOrWhiteSpace(path) || library is null)
            {
                return;
            }

            var key = OrphanedDownloads.Key(path);
            if (key.Length == 0 || folders.ContainsKey(key)
                || protectedRoots.Any(root => string.Equals(key, root, StringComparison.OrdinalIgnoreCase)
                    || key.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
                    || root.StartsWith(key + "/", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            folders[key] = (path.TrimEnd('\\', '/'), library.Id, library.Name);
        }

        foreach (var library in libraries)
        {
            Add(library.DownloadsPath, library);
        }

        foreach (var client in clientSnapshots)
        {
            var items = client.Queue.Select(item => (item.SourcePath, item.LibraryId, item.MediaType))
                .Concat(client.History.Select(item => (item.SourcePath, LibraryId: (string?)null, item.MediaType)));
            foreach (var (sourcePath, libraryId, mediaType) in items)
            {
                if (string.IsNullOrWhiteSpace(sourcePath))
                {
                    continue;
                }

                var key = OrphanedDownloads.Key(sourcePath);
                var slash = key.LastIndexOf('/');
                if (slash <= 0)
                {
                    continue;
                }

                var library = libraries.FirstOrDefault(item => string.Equals(item.Id, libraryId, StringComparison.OrdinalIgnoreCase));
                if (library is null)
                {
                    var ofType = libraries.Where(item => string.Equals(item.MediaType, mediaType, StringComparison.OrdinalIgnoreCase)).ToArray();
                    library = ofType.Length == 1 ? ofType[0] : null;
                }

                // Parent in the client's own separators, so the path read is the one it wrote.
                var raw = sourcePath.TrimEnd('\\', '/');
                var cut = Math.Max(raw.LastIndexOf('\\'), raw.LastIndexOf('/'));
                Add(cut > 0 ? raw[..cut] : null, library);
            }
        }

        return folders.Values.ToArray();
    }

    private IReadOnlyList<DownloadFolderEntry> ReadTopLevel(string downloadsPath)
    {
        try
        {
            if (!Directory.Exists(downloadsPath))
            {
                return [];
            }

            return new DirectoryInfo(downloadsPath)
                .EnumerateFileSystemInfos()
                .Select(info => new DownloadFolderEntry(
                    info.FullName,
                    info is DirectoryInfo,
                    0,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)))
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not read download folder {Path}.", downloadsPath);
            return [];
        }
    }

    private static long SizeOf(string path)
    {
        try
        {
            return Directory.Exists(path)
                ? new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length)
                : File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string FormatSize(long bytes)
        => bytes >= 1_073_741_824 ? $"{bytes / 1_073_741_824d:0.0} GB" : $"{bytes / 1_048_576d:0} MB";
}
