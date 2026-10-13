using System.Text.RegularExpressions;

namespace Deluno.Platform.Tests;

/// <summary>
/// #1188: a person's media only ever leaves a library through Deluno's recycle bin
/// (<c>IRecycleBinService</c>), so it can be restored. A film's Files tab used to call
/// <c>File.Delete</c> and the video was gone for good.
///
/// <para>This test lists every <c>File.Delete</c> and <c>Directory.Delete</c> in <c>src</c> with the
/// reason it cannot reach library media. A new one in a file that is not listed, or one more in a
/// listed file, fails here. The fix is to move the file with
/// <c>IRecycleBinService.StoreRemovedFileAsync</c> (one file) or <c>MoveAsync</c> (a whole title);
/// only if it truly cannot touch library media is it added below, with the reason.</para>
/// </summary>
public sealed class LibraryMediaLeavesOnlyThroughTheRecycleBinTests
{
    private const string OwnData = "Deluno's own data, logs, probe or temporary file; never a library file.";
    private const string DownloadArea = "A download area or a processor's second copy, never a library file (guarded by the download/processor folder, and refused inside a library).";

    /// <summary>Repository-relative path to the most deletes it may hold, with why none is library media.</summary>
    private static readonly Dictionary<string, (int Max, string Reason)> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["src/Deluno.Api/Backup/DelunoBackupService.cs"] = (4, "Backup archives, the restore staging folder and snapshot folders under Deluno's data root. " + OwnData),
        ["src/Deluno.Api/Health/DelunoReadinessService.cs"] = (1, "A write-probe file Deluno creates itself. " + OwnData),
        ["src/Deluno.Api/System/SystemLogFilesEndpointRouteBuilderExtensions.cs"] = (1, "Deluno's own log files. " + OwnData),
        ["src/Deluno.Companions/CompanionDownloads.cs"] = (1, "A companion installer download. " + OwnData),
        ["src/Deluno.Companions/CompanionTools.cs"] = (2, "A companion installer download. " + OwnData),
        ["src/Deluno.Contracts/StateFiles/StateFile.cs"] = (1, "The one shared state-file writer: the real disk behind it, deleting only the temporary file it wrote beside a state file, or a state file it was asked to clear. " + OwnData),
        ["src/Deluno.Filesystem/FilesystemEndpointRouteBuilderExtensions.cs"] = (2, "Leftover files of a season pack, refused unless under a configured download path; and a write-probe file. " + DownloadArea),
        ["src/Deluno.Filesystem/ImportPipelineService.cs"] = (10, "Import rollback of a file the import itself just placed, the source/processor copy after a successful import, and empty download or hand-off folders. The library file it replaces goes through IRecycleBinService.StoreReplacementAsync, or is left in place when no library matches it (#1188)."),
        ["src/Deluno.Filesystem/ImportRestartRecoveryService.cs"] = (1, "An import marker file. " + OwnData),
        ["src/Deluno.Filesystem/LibraryAvailabilityService.cs"] = (2, "A write-probe file. " + OwnData),
        ["src/Deluno.Filesystem/ProcessorHandoffOutputCleanup.cs"] = (2, "A processor's partial output and empty folders inside its output folder. " + DownloadArea),
        ["src/Deluno.Filesystem/UnmatchedImportGiveUpService.cs"] = (1, "Only a file that is NOT inside any library root; one inside a library goes to the recycle bin (#1188)."),
        ["src/Deluno.Infrastructure/Storage/StagedRestore.cs"] = (4, "Restore markers and staging under the data root. " + OwnData),
        ["src/Deluno.Integrations/DownloadClients/ClientRemovalGuard.cs"] = (3, "A finished usenet job's own folder (or its one finished file), and the empty temporary folders Transmission leaves beside a removed download (#1224), asked for by the person's 'delete with data' when the client's own delete leaves it (#1212); only a folder named after that job, never a filesystem root. " + DownloadArea),
        ["src/Deluno.Integrations/DownloadClients/Clients/RtorrentDownloadClient.cs"] = (2, "The download's own data, asked for by the person's 'delete with data' on a download; rTorrent has no delete-with-data of its own. " + DownloadArea),
        ["src/Deluno.Integrations/DownloadClients/DownloadClientTelemetryService.cs"] = (2, "A refused download's payload, only inside the library's download path. " + DownloadArea),
        ["src/Deluno.Integrations/DownloadClients/DownloadFolderChangeNotice.cs"] = (1, "A notice state file. " + OwnData),
        ["src/Deluno.Integrations/DownloadClients/OrphanedDownloadService.cs"] = (2, "An orphaned download; refuses anything inside a library root. " + DownloadArea),
        ["src/Deluno.Integrations/DownloadClients/RefusedDownloadCleanupService.cs"] = (2, "Leftovers of a refused release; the sweep is given the library roots and skips them. " + DownloadArea),
        ["src/Deluno.Integrations/Metadata/TmdbMetadataProvider.cs"] = (2, "Cached artwork under the data root. " + OwnData),
        ["src/Deluno.Integrations/Search/Trackers/TrackerDefinitionUpdateService.cs"] = (1, "A tracker definition marker. " + OwnData),
        ["src/Deluno.Media/SubtitleRejection.cs"] = (1, "A subtitle Deluno itself fetched and a person or the sync check rejected; it is blocklisted so it is never offered again."),
        ["src/Deluno.Platform/ExternalIntegrationEndpointRouteBuilderExtensions.cs"] = (2, "A person's 'delete' on one item directly inside a library's processor output folder. " + DownloadArea),
        ["src/Deluno.Platform/Processing/ProcessorOutputPathPolicy.cs"] = (3, "A processor's second copy of a download already imported, inside the library's output folder. " + DownloadArea),
        ["src/Deluno.Platform/RecycleBin.cs"] = (4, "The recycle bin itself: permanent deletion of an item by the person, retention, and empty holding folders."),
        ["src/Deluno.Worker/Services/ProcessorHandoffWatcher.cs"] = (1, "A processor's partial output that is not ready. " + DownloadArea),
        ["src/Deluno.Worker/Services/ProcessorOutputSweep.cs"] = (2, "Stale processor output inside the library's output folder. " + DownloadArea),
    };

    private static readonly Regex Delete = new(@"\b(?:File|Directory)\.Delete\(", RegexOptions.Compiled);

    [Fact]
    public void Every_delete_in_the_product_is_either_listed_with_a_reason_or_goes_through_the_recycle_bin()
    {
        var root = RepositoryRoot();
        var problems = new List<string>();
        var sep = Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                     .Where(file => !file.Contains($"{sep}obj{sep}") && !file.Contains($"{sep}bin{sep}")))
        {
            var count = File.ReadLines(file)
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Sum(line => Delete.Matches(line).Count);
            if (count == 0)
            {
                continue;
            }

            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (!Allowed.TryGetValue(relative, out var allowed))
            {
                problems.Add($"{relative}: {count} unlisted File.Delete/Directory.Delete. Library media must leave through IRecycleBinService; if this cannot touch library media, list it here with the reason.");
            }
            else if (count > allowed.Max)
            {
                problems.Add($"{relative}: {count} deletes, {allowed.Max} listed. Check the new one cannot reach library media (or use IRecycleBinService), then raise the count with a reason.");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }


    /// <summary>
    /// #1188: a private <c>TryDelete(path)</c> wrapper hid a real <c>File.Delete</c> from the count above (a pack
    /// import deleted the library file it replaced and its subtitles with it). Calls to a wrapper are counted
    /// here, per file, so a new one cannot slip in. The eleven in the import pipeline are staging and backup
    /// artifacts Deluno itself created, plus the fallbacks of an instance built without a recycle bin (the host
    /// always supplies one); a replaced library video is never deleted when no library matches, it is left alone.
    /// </summary>
    private static readonly Dictionary<string, int> AllowedWrapperCalls = new(StringComparer.OrdinalIgnoreCase)
    {
        ["src/Deluno.Companions/CompanionDownloads.cs"] = 2,
        ["src/Deluno.Filesystem/ImportPipelineService.cs"] = 11,
        ["src/Deluno.Filesystem/ImportRestartRecoveryService.cs"] = 10,
    };

    private static readonly Regex WrapperCall = new(@"\bTryDelete\(", RegexOptions.Compiled);
    private static readonly Regex WrapperDefinition = new(@"\b(?:void|bool)\s+TryDelete\(", RegexOptions.Compiled);

    [Fact]
    public void A_private_delete_wrapper_is_counted_like_a_direct_delete()
    {
        var root = RepositoryRoot();
        var problems = new List<string>();
        var sep = Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                     .Where(file => !file.Contains($"{sep}obj{sep}") && !file.Contains($"{sep}bin{sep}")))
        {
            var count = File.ReadLines(file)
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal) && !WrapperDefinition.IsMatch(line))
                .Sum(line => WrapperCall.Matches(line).Count);
            if (count == 0)
            {
                continue;
            }

            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (!AllowedWrapperCalls.TryGetValue(relative, out var max))
            {
                problems.Add($"{relative}: {count} TryDelete calls, none listed. Library media must leave through IRecycleBinService.");
            }
            else if (count > max)
            {
                problems.Add($"{relative}: {count} TryDelete calls, {max} listed. Check the new one cannot reach library media, then raise the count.");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
    [Fact]
    public void A_films_Files_tab_and_an_episodes_file_delete_go_through_the_recycle_bin()
    {
        var root = RepositoryRoot();
        foreach (var path in new[]
                 {
                     "src/Deluno.Filesystem/MovieFilesEndpointRouteBuilderExtensions.cs",
                     "src/Deluno.Filesystem/EpisodeFilesEndpointRouteBuilderExtensions.cs",
                 })
        {
            var text = File.ReadAllText(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
            Assert.DoesNotContain("File.Delete(", text);
            Assert.Contains("StoreRemovedFileAsync(", text);
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "Deluno.Infrastructure")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
