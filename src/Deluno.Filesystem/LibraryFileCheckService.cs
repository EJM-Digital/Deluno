using System.Text.Json;
using Deluno.Jobs.Data;
using Microsoft.Extensions.Logging;

namespace Deluno.Filesystem;

/// <param name="MarkedMissing">Tracked files that were not on disk any more.</param>
/// <param name="UnreachableRoots">
/// Libraries whose root could not be read at all. Counted separately and
/// never repaired: an unmounted drive must not be mistaken for a library of
/// deleted files.
/// </param>
public sealed record LibraryFileCheckResult(int MarkedMissing, int UnreachableRoots);

public interface ILibraryFileCheckService
{
    Task<LibraryFileCheckResult> RunAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Looking at whether the files Deluno thinks you have are still there.
///
/// <para>Deluno never looked at a file again after importing it, so a film you
/// deleted yourself still showed as held, was never searched for, and answered
/// "you already have this" when asked why it would not download. Three wrong
/// answers, all sounding certain. DESIGN-007 decisions 11 and 18.</para>
///
/// <para><b>Only the missing-file repair is applied.</b> An orphan file or a
/// leftover staging artifact needs a judgement Deluno should not make on its
/// own; marking a tracked file missing needs none, because it only ever
/// corrects Deluno's own note and never touches disk. That asymmetry is the
/// reason this can run unattended.</para>
///
/// <para><b>Why it is a service and not a method on the worker.</b> James:
/// <i>"a user can manually trigger a refresh of the library and it should come
/// up as missing and then the user can manually trigger a search"</i>. The
/// button and the schedule have to do the identical thing — the moment they are
/// two implementations, the answer starts depending on which one ran, which is
/// the failure this whole design is about.</para>
/// </summary>
public sealed class LibraryFileCheckService(
    IFilesystemReconciliationService reconciliation,
    IActivityFeedRepository activityFeedRepository,
    ILogger<LibraryFileCheckService> logger,
    Deluno.Series.Data.ISeriesCatalogRepository? seriesCatalogRepository = null) : ILibraryFileCheckService
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<LibraryFileCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        var report = await reconciliation.ScanAsync(cancellationToken);
        var missing = report.Issues
            .Where(issue => issue.Kind == "missingTrackedFile")
            .ToArray();
        var unreachable = report.Issues.Count(issue => issue.Kind == "libraryRootUnreachable");

        var corrected = 0;
        var droppedOut = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var issue in missing)
        {
            // A library whose root vanished part-way through the pass: every
            // repair re-checks the root right before writing, and once one
            // refuses, the rest of that library are not even tried (#1065).
            if (droppedOut.Contains(issue.LibraryId))
            {
                continue;
            }

            var repair = await reconciliation.RepairAsync(
                new FilesystemReconciliationRepairRequest(issue.Id, "mark-missing"),
                cancellationToken);
            if (repair.LibraryUnreachable)
            {
                droppedOut.Add(issue.LibraryId);
                unreachable++;
                continue;
            }

            if (repair.Repaired)
            {
                corrected++;
            }
        }

        // An episode that shows no file while its own, correctly named file sits in its
        // show's folder is linked to it (#1174), the same as Review existing files does.
        var linked = seriesCatalogRepository is null
            ? []
            : await seriesCatalogRepository.RelinkEpisodesWithTheirOwnFilesAsync(cancellationToken);
        foreach (var episode in linked)
        {
            await activityFeedRepository.RecordActivityAsync(
                "series.episode-file.linked",
                $"{episode.SeriesTitle} S{episode.SeasonNumber:00}E{episode.EpisodeNumber:00} showed no file although its own was in the show's folder. It now shows {Path.GetFileName(episode.FilePath)}.",
                null, null, "episode", episode.EpisodeId, cancellationToken);
        }

        // Nothing changed, nothing said. A pass that announces itself every six
        // hours teaches people to stop reading the activity feed.
        if (corrected == 0 && unreachable == 0)
        {
            return new LibraryFileCheckResult(0, 0);
        }

        logger.LogInformation(
            "Library file check marked {CorrectedCount} tracked file(s) missing and found {UnreachableCount} unreachable library root(s).",
            corrected,
            unreachable);

        // Worth telling somebody about: a title going from held to missing is a
        // thing they will see on a shelf, and they should learn it from Deluno
        // rather than from a gap. Each affected title also gets its own,
        // named entry (RecordMarkMissingRepairAsync in the reconciliation
        // service); this is the summary of the pass as a whole.
        if (corrected > 0)
        {
            await activityFeedRepository.RecordActivityAsync(
                "library.file.missing",
                corrected == 1
                    ? "A file Deluno was holding is no longer on disk. That title is now missing and unmonitored, so Deluno stopped wanting it."
                    : $"{corrected} files Deluno was holding are no longer on disk. Those titles are now missing and unmonitored, so Deluno stopped wanting them.",
                JsonSerializer.Serialize(new { corrected, unreachable }, PayloadJsonOptions),
                null,
                "library",
                null,
                cancellationToken);
        }

        return new LibraryFileCheckResult(corrected, unreachable);
    }
}
