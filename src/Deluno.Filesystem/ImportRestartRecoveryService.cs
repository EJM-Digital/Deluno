using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Platform;
using Microsoft.Extensions.Logging;

namespace Deluno.Filesystem;

/// <summary>
/// What an in-flight import was doing when Deluno stopped, recorded next to
/// its staging file so a restart can finish or undo it (#913 / cleanup sheet
/// K4).
/// </summary>
public sealed record ImportRestartMarker(
    string SourcePath,
    string DestinationPath,
    string StagingPath,
    string MediaType,
    string Title);

public interface IImportRestartRecoveryService
{
    /// <summary>
    /// Called once on startup. Finds every import a restart interrupted,
    /// finishes the ones whose source or staged content is still there, and
    /// removes and reports the ones that are not. Never leaves a half-copied
    /// file in a library, and never blocklists a release solely because the
    /// app restarted.
    /// </summary>
    Task<ImportRestartRecoveryReport> RecoverAsync(CancellationToken cancellationToken);
}

/// <param name="Skipped">Markers left for the next pass because another program still had them open (#1259).</param>
public sealed record ImportRestartRecoveryReport(int Recovered, int Abandoned, int Skipped = 0);

public sealed class ImportRestartRecoveryService(
    ILibrariesRepository librariesRepository,
    IImportPipelineService importPipelineService,
    IActivityFeedRepository activityFeedRepository,
    ILogger<ImportRestartRecoveryService> logger,
    IRecycleBinService? recycleBinService = null,
    IHealthCheckRepository? healthChecks = null,
    IImportRestartMarkerIo? markerIo = null)
    : IImportRestartRecoveryService
{
    private enum Outcome
    {
        Recovered,
        Abandoned,
        Skipped
    }

    private readonly IImportRestartMarkerIo _io = markerIo ?? RealImportRestartMarkerIo.Instance;

    public async Task<ImportRestartRecoveryReport> RecoverAsync(CancellationToken cancellationToken)
    {
        var recovered = 0;
        var abandoned = 0;
        var busy = new List<(LibraryItem Library, string MarkerPath)>();

        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        foreach (var library in libraries.Where(item => !string.IsNullOrWhiteSpace(item.RootPath)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rootPath = library.RootPath;
            if (!Directory.Exists(rootPath))
            {
                // A drive that has not mounted yet is not a library full of
                // interrupted imports (same reasoning as reconciliation).
                continue;
            }

            foreach (var markerPath in EnumerateMarkerFilesSafe(rootPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (await RecoverOneAsync(library, markerPath, cancellationToken))
                {
                    case Outcome.Recovered:
                        recovered++;
                        break;
                    case Outcome.Abandoned:
                        abandoned++;
                        break;
                    default:
                        busy.Add((library, markerPath));
                        break;
                }
            }
        }

        // A marker another program had open gets one more look once the rest are done; if it is still held it
        // stays exactly as it is for the next recovery. It is never taken for a damaged one.
        var skipped = 0;
        foreach (var (library, markerPath) in busy)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (await RecoverOneAsync(library, markerPath, cancellationToken))
            {
                case Outcome.Recovered:
                    recovered++;
                    break;
                case Outcome.Abandoned:
                    abandoned++;
                    break;
                default:
                    skipped++;
                    break;
            }
        }

        return new ImportRestartRecoveryReport(recovered, abandoned, skipped);
    }

    private async Task<Outcome> RecoverOneAsync(LibraryItem library, string markerPath, CancellationToken cancellationToken)
    {
        // The staging file is named by the marker's own file name, never by anything inside it: a marker that
        // cannot be read still says which file it guards.
        var stagingPath = markerPath[..^ImportRestartMarkerFile.Suffix.Length];
        if (ImportRestartMarkerFile.IsInFlight(stagingPath))
        {
            // An import of this title is running right now in this process; its marker is live, not left over.
            return Outcome.Skipped;
        }

        var read = ImportRestartMarkerFile.Read(markerPath, _io);
        switch (read.Kind)
        {
            case ImportRestartMarkerKind.Missing:
                // Gone between the listing and now: the import finished or someone cleared it. Nothing to do.
                return Outcome.Skipped;
            case ImportRestartMarkerKind.Busy:
                logger.LogInformation(
                    "The import restart marker at {MarkerPath} is open in another program; leaving it and its file for the next pass.",
                    markerPath);
                return Outcome.Skipped;
            case ImportRestartMarkerKind.Unreadable:
                return await RecoverUnreadableAsync(library, markerPath, stagingPath, cancellationToken);
        }

        var marker = read.Marker! with { StagingPath = stagingPath };
        try
        {
            if (File.Exists(marker.DestinationPath))
            {
                if (!File.Exists(stagingPath))
                {
                    TryDelete(markerPath);
                    await RecordAsync(marker, finished: true, "the file was already in its library folder", cancellationToken);
                    return Outcome.Recovered;
                }

                if (StagingIsAccountedFor(marker, stagingPath))
                {
                    // The file was actually placed before the restart - only the marker and a leftover staging
                    // copy of it never got cleaned up.
                    TryDelete(stagingPath);
                    TryDelete(markerPath);
                    await RecordAsync(marker, finished: true, "the file was already in its library folder", cancellationToken);
                    return Outcome.Recovered;
                }

                // A different file is already in the library and this staging file is not a copy of it: it may be
                // the only copy of the new media. Deluno does not delete what it cannot account for.
                await KeepMediaAsync(library, stagingPath, marker.Title, "a different file was already in the library folder", cancellationToken);
                TryDelete(markerPath);
                return Outcome.Abandoned;
            }

            // The staged copy (fully verified before it moved to its destination)
            // and the untouched original source are both "the source is still
            // there" for #913's purposes - either way there is a complete file
            // to import from. A "move" transfer leaves only the staged copy;
            // "copy"/"hardlink" leave the original untouched throughout.
            var recoverableSourcePath =
                File.Exists(stagingPath) ? stagingPath :
                File.Exists(marker.SourcePath) ? marker.SourcePath :
                null;

            if (recoverableSourcePath is null)
            {
                // Neither the staging file nor the original exists: there is no media left to protect.
                TryDelete(markerPath);
                await RecordAsync(marker, finished: false, "neither the download nor the partly-copied file could be found", cancellationToken);
                return Outcome.Abandoned;
            }

            if (!string.Equals(recoverableSourcePath, marker.SourcePath, StringComparison.Ordinal) &&
                !File.Exists(marker.SourcePath))
            {
                // A "move" transfer already consumed the original path - put the
                // staged content back where the source was so the normal import
                // pipeline (matching, naming, catalogue update) runs exactly as
                // it would for a fresh download.
                Directory.CreateDirectory(Path.GetDirectoryName(marker.SourcePath) ?? ".");
                File.Move(recoverableSourcePath, marker.SourcePath, overwrite: false);
            }

            // The original is in place now (put back above, or never moved), so a staging file still here is
            // only a redundant copy of it.
            if (File.Exists(marker.SourcePath))
            {
                TryDelete(stagingPath);
            }

            TryDelete(markerPath);

            var request = new ImportExecuteRequest(
                new ImportPreviewRequest(
                    marker.SourcePath,
                    FileName: null,
                    MediaType: marker.MediaType,
                    Title: null,
                    Year: null,
                    Genres: null,
                    Tags: null,
                    Studio: null,
                    OriginalLanguage: null),
                TransferMode: "auto",
                Overwrite: false,
                AllowCopyFallback: true);

            var result = await importPipelineService.ExecuteAsync(request, cancellationToken);
            if (result.Succeeded)
            {
                await RecordAsync(marker, finished: true, "the source was still there, so Deluno imported it again", cancellationToken);
                return Outcome.Recovered;
            }

            await RecordAsync(marker, finished: false, result.Message, cancellationToken);
            return Outcome.Abandoned;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Whatever failed, the staging file may still be the only copy of the media: it stays where it is,
            // and so does the marker, so the next recovery tries again.
            logger.LogWarning(exception, "Restart recovery could not finish the import for {Title}.", marker.Title);
            await RecordAsync(marker, finished: false, exception.Message, cancellationToken);
            await RaiseNoticeAsync(library, stagingPath, marker.Title, recycled: false, "Deluno could not finish it", cancellationToken);
            return Outcome.Abandoned;
        }
    }

    /// <summary>
    /// The marker was read but is truncated or not a marker. Whether the staging file is the only copy of a "move"
    /// import cannot be known, so it is kept (recycle bin, or in place) and the person is told; only then is the
    /// damaged marker removed (#1259).
    /// </summary>
    private async Task<Outcome> RecoverUnreadableAsync(
        LibraryItem library,
        string markerPath,
        string stagingPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(stagingPath))
        {
            // A damaged note about a file that is not there: no media to protect.
            logger.LogWarning("The import restart marker at {MarkerPath} is damaged and its file is gone; removing the marker.", markerPath);
            TryDelete(markerPath);
            return Outcome.Abandoned;
        }

        await KeepMediaAsync(library, stagingPath, Path.GetFileName(stagingPath), "its restart note was damaged", cancellationToken);
        TryDelete(markerPath);
        return Outcome.Abandoned;
    }

    private static bool StagingIsAccountedFor(ImportRestartMarker marker, string stagingPath)
    {
        try
        {
            // The same size as what is in the library, or the original download is still there to import from.
            return new FileInfo(stagingPath).Length == new FileInfo(marker.DestinationPath).Length
                || File.Exists(marker.SourcePath);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Deluno never deletes media it cannot account for. The file goes to the recycle bin where it can be restored,
    /// or, when the bin cannot take it, stays exactly where it is. Either way the person gets a line in Activity and
    /// a note in Health.
    /// </summary>
    private async Task KeepMediaAsync(
        LibraryItem library,
        string stagingPath,
        string title,
        string why,
        CancellationToken cancellationToken)
    {
        var recycled = false;
        if (recycleBinService is not null)
        {
            try
            {
                var item = await recycleBinService.StoreRemovedFileAsync(library, stagingPath, null, cancellationToken);
                recycled = item is not null && !File.Exists(stagingPath);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Could not move {StagingPath} to the recycle bin; leaving it in place.", stagingPath);
            }
        }

        await RaiseNoticeAsync(library, stagingPath, title, recycled, why, cancellationToken);
    }

    private async Task RaiseNoticeAsync(
        LibraryItem library,
        string stagingPath,
        string title,
        bool recycled,
        string why,
        CancellationToken cancellationToken)
    {
        var where = recycled
            ? "Deluno put the file in the recycle bin instead of deleting it. Open the recycle bin to restore it."
            : $"Deluno left the file where it is ({stagingPath}). Check it, move it where it belongs or delete it yourself.";
        var message = $"Deluno stopped while importing {title}, and {why}, so it could not tell what to do with the half-finished file. {where}";

        await activityFeedRepository.RecordActivityAsync(
            "filesystem.import.restart-media-kept",
            message + " Nothing was blocklisted.",
            JsonSerializer.Serialize(new { StagingPath = stagingPath, Recycled = recycled, Why = why }),
            null,
            library.MediaType == "tv" ? "series" : "movie",
            null,
            cancellationToken);

        if (healthChecks is not null)
        {
            var id = "notice:import-restart-media:" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(stagingPath)))[..16];
            await healthChecks.UpsertResultsAsync(
                [HealthCheckOutcome.Problem(
                    id,
                    HealthCheckCategories.Notice,
                    $"{title} was kept after a restart",
                    message + " Clear this note once you have dealt with it.",
                    cause: null,
                    fix: null,
                    detailsJson: JsonSerializer.Serialize(new { stagingPath, recycled, libraryId = library.Id }))],
                cancellationToken);
        }
    }

    private async Task RecordAsync(ImportRestartMarker marker, bool finished, string detail, CancellationToken cancellationToken)
    {
        await activityFeedRepository.RecordActivityAsync(
            finished ? "filesystem.import.restart-recovered" : "filesystem.import.restart-abandoned",
            finished
                ? $"Finished an import interrupted by a restart: {marker.Title} ({detail})."
                : $"Could not finish an import interrupted by a restart: {marker.Title} ({detail}). Nothing was blocklisted.",
            JsonSerializer.Serialize(new { marker.SourcePath, marker.DestinationPath, marker.MediaType, Finished = finished, Detail = detail }),
            null,
            marker.MediaType == "tv" ? "series" : "movie",
            null,
            cancellationToken);
    }

    private static IEnumerable<string> EnumerateMarkerFilesSafe(string rootPath)
    {
        try
        {
            return Directory.EnumerateFiles(rootPath, "*" + ImportRestartMarkerFile.Suffix, SearchOption.AllDirectories).ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}
