using System.Text.RegularExpressions;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>
/// #1318: nothing in Deluno deletes media or download data outright. A person's file leaves through the recycle bin, or
/// through the download client's own remove. This reads every source file and fails on a <c>File.Delete</c>,
/// <c>Directory.Delete</c> or <c>FileInfo.Delete</c> (<c>.Delete()</c>) that is not in the recycle bin itself or in the
/// list below. Each entry names the file, how many deletes it may hold, and why those are scratch (Deluno's own temporary,
/// probe, log, state or backup file, or a copy Deluno made itself and is rolling back) or the client's own remove.
/// A new delete, anywhere, fails here until it is routed through the bin or added with a reason a reviewer accepts.
/// </summary>
public sealed class NoCodeDeletesMediaOutrightTests
{
    private static readonly Regex Delete = new(@"\b(?:File|Directory)\.Delete\(|\.Delete\(\)", RegexOptions.Compiled);

    private static readonly Dictionary<string, (int Count, string Reason)> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["src/Deluno.Platform/RecycleBin.cs"] = (4, "The recycle bin itself: permanent deletion after retention, an empty-folder tidy and its own state files."),
        ["src/Deluno.Api/Backup/DelunoBackupService.cs"] = (4, "Deluno's own backup archives and restore staging folders (a person's Delete backup button, retention of Deluno-made archives only, a restore scratch folder)."),
        ["src/Deluno.Api/Health/DelunoReadinessService.cs"] = (1, "The write-probe file Deluno creates to check a folder is writable."),
        ["src/Deluno.Api/System/SystemLogFilesEndpointRouteBuilderExtensions.cs"] = (1, "Deluno's own log files, chosen by name from the System screen."),
        ["src/Deluno.Companions/CompanionDownloads.cs"] = (1, "A companion installer Deluno downloaded to its own temp path."),
        ["src/Deluno.Companions/CompanionTools.cs"] = (2, "Companion installer files Deluno downloaded and ran."),
        ["src/Deluno.Contracts/StateFiles/StateFile.cs"] = (1, "Deluno's own state files under its data folder."),
        ["src/Deluno.Filesystem/FilesystemEndpointRouteBuilderExtensions.cs"] = (1, "The write-probe file Deluno creates to test a folder."),
        ["src/Deluno.Filesystem/ImportPipelineService.cs"] = (10, "Import scratch: staging and temporary copies, the rollback of a copy Deluno placed itself, the broken processed copy (the download it came from is kept), the source after a successful import under the library's own workflow rule, and a processor hand-off's own output folder and temporary files. The replaced film is never deleted here: with no recycle bin it is left in place."),
        ["src/Deluno.Filesystem/ImportRestartRecoveryService.cs"] = (1, "Restart markers and half-written staging files of an import Deluno itself was making."),
        ["src/Deluno.Filesystem/LibraryAvailabilityService.cs"] = (2, "The write-probe file Deluno creates to test a library folder."),
        ["src/Deluno.Filesystem/ProcessorHandoffOutputCleanup.cs"] = (2, "Only a processor's unfinished temporary files (a ready file is never touched) and the folders left empty by that."),
        ["src/Deluno.Infrastructure/Observability/DelunoFileLogger.cs"] = (2, "Deluno's own old log files past their retention."),
        ["src/Deluno.Infrastructure/Storage/StagedRestore.cs"] = (4, "Deluno's own restore marker and staged restore folder."),
        ["src/Deluno.Integrations/DownloadClients/ClientRemovalGuard.cs"] = (3, "The download client's own remove-with-data, finished by Deluno where the client leaves the files behind (a download's own folder, named after the job)."),
        ["src/Deluno.Integrations/DownloadClients/Clients/RtorrentDownloadClient.cs"] = (2, "rTorrent has no delete-with-data of its own, so Deluno does the client's remove-with-data for the download's files."),
        ["src/Deluno.Integrations/DownloadClients/DownloadClientTelemetryService.cs"] = (2, "Purge of a residual payload of a download Deluno removed from its client, only inside the approved download root and only when Deluno owns the payload."),
        ["src/Deluno.Integrations/DownloadClients/DownloadFolderChangeNotice.cs"] = (1, "Deluno's own notice state file."),
        ["src/Deluno.Integrations/DownloadClients/RefusedDownloadCleanupService.cs"] = (2, "The payload of a release Deluno itself refused (it may carry an executable), found by RefusedPayloadSweep which never reaches a library root."),
        ["src/Deluno.Integrations/Metadata/TmdbMetadataProvider.cs"] = (2, "Deluno's own artwork cache files."),
        ["src/Deluno.Integrations/Search/Trackers/TrackerDefinitionUpdateService.cs"] = (1, "Deluno's own tracker-definition marker file."),
        ["src/Deluno.Media/SubtitleRejection.cs"] = (1, "A subtitle file Deluno fetched itself and then judged bad; it is blocklisted so it is never offered again."),
        ["src/Deluno.Platform/Processing/ProcessorOutputPathPolicy.cs"] = (3, "A processor's second copy of a download Deluno has already imported, only inside the library's processor output folder and only when the name matches the download."),
        ["src/Deluno.Worker/Services/ProcessorHandoffWatcher.cs"] = (1, "A crashed processor's unfinished partial output (a ready file is left alone)."),
        ["src/Deluno.Worker/Services/ProcessorOutputSweep.cs"] = (2, "Processor output of a hand-off that will never be imported, by name, inside the processor output folder, and never inside or around a library root."),
    };

    [Fact]
    public void Nothing_in_src_deletes_a_file_or_folder_outright_except_the_recycle_bin_and_the_listed_scratch_paths()
    {
        var root = FindRepositoryRoot();
        var found = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/obj/") || relative.Contains("/bin/"))
            {
                continue;
            }

            var count = File.ReadLines(file)
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Sum(line => Delete.Matches(line).Count);
            if (count > 0)
            {
                found[relative] = count;
            }
        }

        var problems = new List<string>();
        foreach (var (path, count) in found)
        {
            if (!Allowed.TryGetValue(path, out var allowed))
            {
                problems.Add($"{path}: {count} outright delete(s) that are not allow-listed. Route them through the recycle bin, or add the file with a reason.");
            }
            else if (count != allowed.Count)
            {
                problems.Add($"{path}: {count} outright delete(s), the allow-list says {allowed.Count} ({allowed.Reason}). Check the new one is scratch, then update the count.");
            }
        }

        foreach (var path in Allowed.Keys.Where(path => !found.ContainsKey(path)))
        {
            problems.Add($"{path} no longer deletes anything outright: remove it from the allow-list.");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Every_allow_listed_file_has_a_reason()
    {
        Assert.All(Allowed, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Value.Reason), entry.Key));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Deluno.slnx"))) return directory.FullName;
        }

        throw new InvalidOperationException("Could not find the repository root.");
    }
}
