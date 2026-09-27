using System.Text.Json;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
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

public sealed record ImportRestartRecoveryReport(int Recovered, int Abandoned);

public sealed class ImportRestartRecoveryService(
    ILibrariesRepository librariesRepository,
    IImportPipelineService importPipelineService,
    IActivityFeedRepository activityFeedRepository,
    ILogger<ImportRestartRecoveryService> logger)
    : IImportRestartRecoveryService
{
    public async Task<ImportRestartRecoveryReport> RecoverAsync(CancellationToken cancellationToken)
    {
        var recovered = 0;
        var abandoned = 0;

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
                var outcome = await RecoverOneAsync(markerPath, cancellationToken);
                if (outcome)
                {
                    recovered++;
                }
                else
                {
                    abandoned++;
                }
            }
        }

        return new ImportRestartRecoveryReport(recovered, abandoned);
    }

    private async Task<bool> RecoverOneAsync(string markerPath, CancellationToken cancellationToken)
    {
        var stagingPath = markerPath[..^".deluno-marker.json".Length];
        ImportRestartMarker? marker;
        try
        {
            marker = JsonSerializer.Deserialize<ImportRestartMarker>(File.ReadAllText(markerPath));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not read the import restart marker at {MarkerPath}.", markerPath);
            marker = null;
        }

        if (marker is null)
        {
            TryDelete(markerPath);
            TryDelete(stagingPath);
            return false;
        }

        try
        {
            if (File.Exists(marker.DestinationPath))
            {
                // The file was actually placed before the restart - only the
                // marker and any leftover staging copy never got cleaned up.
                TryDelete(stagingPath);
                TryDelete(markerPath);
                await RecordAsync(marker, finished: true, "the file was already in its library folder", cancellationToken);
                return true;
            }

            // The staged copy (fully verified before it moved to its destination)
            // and the untouched original source are both "the source is still
            // there" for #913's purposes - either way there is a complete file
            // to import from. A "move" transfer leaves only the staged copy;
            // "copy"/"hardlink" leave the original untouched throughout.
            var recoverableSourcePath =
                File.Exists(marker.StagingPath) ? marker.StagingPath :
                File.Exists(marker.SourcePath) ? marker.SourcePath :
                null;

            if (recoverableSourcePath is null)
            {
                TryDelete(stagingPath);
                TryDelete(markerPath);
                await RecordAsync(marker, finished: false, "neither the download nor the partly-copied file could be found", cancellationToken);
                return false;
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

            // Whatever is left of the staging file at this point is either
            // already moved (above) or redundant now that the original
            // source is what the fresh import below will read - either way
            // it must not linger.
            TryDelete(marker.StagingPath);
            TryDelete(markerPath);

            var mediaType = marker.MediaType;
            var request = new ImportExecuteRequest(
                new ImportPreviewRequest(
                    marker.SourcePath,
                    FileName: null,
                    MediaType: mediaType,
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
                return true;
            }

            await RecordAsync(marker, finished: false, result.Message, cancellationToken);
            return false;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Restart recovery could not finish the import for {Title}.", marker.Title);
            TryDelete(stagingPath);
            TryDelete(markerPath);
            await RecordAsync(marker, finished: false, exception.Message, cancellationToken);
            return false;
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
            return Directory.EnumerateFiles(rootPath, "*.deluno-marker.json", SearchOption.AllDirectories).ToArray();
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
