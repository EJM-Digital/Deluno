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
    ILogger<LibraryFileCheckService> logger) : ILibraryFileCheckService
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
        foreach (var issue in missing)
        {
            var repair = await reconciliation.RepairAsync(
                new FilesystemReconciliationRepairRequest(issue.Id, "mark-missing"),
                cancellationToken);
            if (repair.Repaired)
            {
                corrected++;
            }
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
        // rather than from a gap.
        if (corrected > 0)
        {
            await activityFeedRepository.RecordActivityAsync(
                "library.file.missing",
                corrected == 1
                    ? "A file Deluno was holding is no longer on disk. That title is now missing and will be searched for again."
                    : $"{corrected} files Deluno was holding are no longer on disk. Those titles are now missing and will be searched for again.",
                JsonSerializer.Serialize(new { corrected, unreachable }, PayloadJsonOptions),
                null,
                "library",
                null,
                cancellationToken);
        }

        return new LibraryFileCheckResult(corrected, unreachable);
    }
}
