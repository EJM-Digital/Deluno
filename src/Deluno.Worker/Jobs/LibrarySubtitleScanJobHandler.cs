using System.Text.Json;
using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Media;

namespace Deluno.Worker.Jobs;

/// <summary>
/// Reads what subtitles the files in a library already have, a slice at a time.
///
/// Subber knows about the subtitles it fetches because it writes them itself,
/// at the moment it writes them. This is the other half of the same question,
/// and on the day somebody first asks a shelf for English it is the whole of
/// it: every file already on disk, with whatever came with it. Without this,
/// turning a language on paints the library red for subtitles that are sitting
/// right beside the video.
///
/// <para><b>It is not a scheduler.</b> DESIGN-002 rule 3 — no second scheduler,
/// no second lane, no second worker. Nothing here decides when to run. The
/// library automation cycle plans this exactly as it plans a search, so it
/// inherits the time-of-day window, the interval and the manual override, and
/// cannot drift from them. A second copy of a scheduling rule is how the last
/// four defects in this codebase were built.</para>
///
/// <para><b>It is sliced,</b> for the reason
/// <see cref="LibraryImportExistingJobHandler"/> is: a library of twenty
/// thousand episodes is an hour of ffprobe, and a handler that did it in one
/// lease would look like a stalled worker and lose the lot on a restart. Each
/// slice commits what it read, so a restart costs at most one slice.</para>
///
/// <para><b>It costs nothing to a library that has not asked for subtitles.</b>
/// No languages wanted, no job planned, and this handler returns without
/// touching a disk even if one reaches it.</para>
/// </summary>
public sealed class LibrarySubtitleScanJobHandler(
    ILibrariesRepository librariesRepository,
    IMediaSubtitleRepository mediaSubtitleRepository,
    ISubtitleInventoryService subtitleInventoryService,
    IJobScheduler jobScheduler,
    TimeProvider timeProvider)
    : IJobHandler
{
    /// <summary>
    /// How many files one slice reads.
    ///
    /// Deliberately not the library's <c>MaxItemsPerRun</c>, which exists to
    /// bound outbound requests to an indexer. This is local disk and a local
    /// process; a shelf that searches ten titles an hour would take eight years
    /// to read twenty thousand files at that rate.
    /// </summary>
    public const int SliceSize = 100;

    public string JobType => "library.subtitles.scan";

    public async Task<string> HandleAsync(JobQueueItem job, CancellationToken cancellationToken)
    {
        var payload = ParsePayload(job.PayloadJson);
        if (payload is null || string.IsNullOrWhiteSpace(payload.LibraryId))
        {
            throw new InvalidOperationException("Subtitle scan job payload could not be read.");
        }

        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        var library = libraries.FirstOrDefault(item => item.Id == payload.LibraryId);
        if (library is null)
        {
            return "That library no longer exists, so nothing was scanned.";
        }

        var languages = library.SubtitleLanguages ?? [];
        if (languages.Count == 0)
        {
            return $"{library.Name} is not asking for any subtitle languages, so nothing was scanned.";
        }

        var kind = string.Equals(library.MediaType, "tv", StringComparison.OrdinalIgnoreCase)
            ? MediaKind.Series
            : MediaKind.Movie;

        var candidates = await mediaSubtitleRepository.ListPendingScansAsync(
            kind,
            library.Id,
            SliceSize,
            cancellationToken);

        if (candidates.Count == 0)
        {
            return $"Every file in {library.Name} has been read for subtitles.";
        }

        var scanned = 0;
        var withSubtitles = 0;
        var missingFiles = 0;
        var probeUnavailable = 0;

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var inventory = await subtitleInventoryService.InspectAsync(candidate.FilePath, cancellationToken);
            if (!inventory.VideoExists)
            {
                // The file has gone. Reconciliation owns that; recording a scan
                // of nothing here would only make this slice loop on it.
                missingFiles++;
                continue;
            }

            if (inventory.ProbeStatus == "unavailable")
            {
                probeUnavailable++;
            }

            await mediaSubtitleRepository.RecordScanAsync(
                kind,
                candidate.MediaId,
                new MediaSubtitleScan(
                    FilePath: candidate.FilePath,
                    FileSizeBytes: candidate.FileSizeBytes,
                    ProbeStatus: inventory.ProbeStatus,
                    SubtitleCount: inventory.Subtitles.Count,
                    ScannedUtc: timeProvider.GetUtcNow()),
                [.. inventory.Subtitles.Select(subtitle => new MediaSubtitleRow(
                    Language: subtitle.Language,
                    Source: subtitle.Source,
                    Forced: subtitle.Forced,
                    HearingImpaired: subtitle.HearingImpaired,
                    FilePath: subtitle.Path,
                    StreamIndex: subtitle.StreamIndex,
                    Codec: subtitle.Codec,
                    Provider: null))],
                cancellationToken);

            scanned++;
            if (inventory.Subtitles.Count > 0)
            {
                withSubtitles++;
            }
        }

        if (candidates.Count == SliceSize)
        {
            await jobScheduler.EnqueueAsync(
                new EnqueueJobRequest(
                    JobType: JobType,
                    Source: library.MediaType,
                    PayloadJson: job.PayloadJson,
                    RelatedEntityType: "library",
                    RelatedEntityId: library.Id,
                    DedupeKey: $"library.subtitles.scan:{library.Id}"),
                cancellationToken);
        }

        return BuildSummary(library.Name, scanned, withSubtitles, missingFiles, probeUnavailable);
    }

    /// <summary>
    /// Written for the person reading Activity, who wants to know what Deluno
    /// found rather than how many rows it touched — and told plainly when
    /// ffprobe is missing, because that install can only ever see half the
    /// subtitles it owns and nothing else would say so.
    /// </summary>
    private static string BuildSummary(string libraryName, int scanned, int withSubtitles, int missingFiles, int probeUnavailable)
    {
        if (scanned == 0)
        {
            return missingFiles > 0
                ? $"Read {libraryName} for subtitles. {missingFiles} tracked file(s) were not on disk."
                : $"Read {libraryName} for subtitles and found nothing new to read.";
        }

        var summary = $"Read {scanned} file(s) in {libraryName} for subtitles; {withSubtitles} already had at least one.";
        if (missingFiles > 0)
        {
            summary += $" {missingFiles} tracked file(s) were not on disk.";
        }

        if (probeUnavailable > 0)
        {
            summary += " ffprobe is not installed, so subtitles inside the video files could not be read — only files beside them.";
        }

        return summary;
    }

    private static LibrarySubtitleScanPayload? ParsePayload(string? payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<LibrarySubtitleScanPayload>(payloadJson ?? "{}", JobPayloads.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record LibrarySubtitleScanPayload(string LibraryId, string? LibraryName, string? MediaType);
}
