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
    IMediaStateRepository mediaStateRepository,
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

    /// <summary>
    /// How long a file's reading stays good for.
    ///
    /// <para><b>Why there is a cadence at all.</b> A scan used to run again only
    /// when the video changed — a new path, a new size, or a probe that never
    /// happened. Deleting the <c>.srt</c> beside it changes none of those, so
    /// the row saying English was held stood for ever and the shelf went on
    /// reporting that every file had what you asked for. The commoner half is
    /// the same blind spot in reverse: a subtitle dropped in by hand was never
    /// noticed either.</para>
    ///
    /// <para><b>Why twelve hours, and why it is not a setting.</b> The standing
    /// check asks whether Deluno can decide and explain the consequence once,
    /// and here it can: a re-read costs a directory listing, not an ffprobe, so
    /// there is no trade-off to hand anybody. Bazarr exposes <i>"use cached
    /// embedded subtitles parser results"</i> precisely because it re-parses
    /// containers on every pass and had to give people a way to stop it. Deluno
    /// already records what the video was, so it can tell the expensive half
    /// from the cheap one without being told — see
    /// <see cref="MediaSubtitleScanCandidate.VideoChanged"/>.</para>
    ///
    /// <para>Half a day is the longest a person should wait to see a subtitle
    /// they dropped in by hand, and short enough that a deletion corrects itself
    /// the same day. A shelf nobody touches costs one listing per file per
    /// twelve hours, and a shelf asking for no languages costs nothing at
    /// all.</para>
    /// </summary>
    public static readonly TimeSpan RereadAfter = TimeSpan.FromHours(12);

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

        // Empty means "do not guess", which is the default and today's
        // behaviour. Normalised because a person types "English" and everything
        // else in Deluno says "en".
        var unknownLanguage = string.IsNullOrWhiteSpace(library.SubtitleUnknownLanguage)
            ? null
            : SubtitleLanguages.Normalize(library.SubtitleUnknownLanguage);

        var now = timeProvider.GetUtcNow();
        var candidates = await mediaSubtitleRepository.ListPendingScansAsync(
            kind,
            library.Id,
            SliceSize,
            now - RereadAfter,
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

            var inventory = await subtitleInventoryService.InspectAsync(
                candidate.FilePath,
                probeContainer: candidate.VideoChanged,
                cancellationToken);

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

            var recorded = candidate.VideoChanged
                ? []
                : await mediaSubtitleRepository.ListSubtitlesAsync(kind, candidate.MediaId, cancellationToken);

            var found = inventory.Subtitles.Select(subtitle => new MediaSubtitleRow(
                    // The one place a bare `Movie.srt` is given a language, and
                    // only because somebody said what it is.
                    //
                    // `SubtitleInventoryService` refuses to guess, and that
                    // stands: it reports `und`. This applies the library's own
                    // answer, which is empty by default and therefore changes
                    // nothing on an install that has not set it. Bazarr does
                    // exactly this and it is the missing half of a decision
                    // DESIGN-002 left open (#321).
                    Language: subtitle.Language == SubtitleLanguages.Unknown && unknownLanguage is not null
                        ? unknownLanguage
                        : subtitle.Language,
                    Source: subtitle.Source,
                    Forced: subtitle.Forced,
                    HearingImpaired: subtitle.HearingImpaired,
                    FilePath: subtitle.Path,
                    StreamIndex: subtitle.StreamIndex,
                    Codec: subtitle.Codec,
                    Provider: null)).ToArray();

            var rows = WholeTruth(candidate.VideoChanged, recorded, found);

            await mediaSubtitleRepository.RecordScanAsync(
                kind,
                candidate.MediaId,
                new MediaSubtitleScan(
                    FilePath: candidate.FilePath,
                    FileSizeBytes: candidate.FileSizeBytes,
                    ProbeStatus: inventory.ProbeStatus,
                    SubtitleCount: rows.Count,
                    ScannedUtc: now),
                rows,
                cancellationToken);

            // The other half of the same probe.
            //
            // ffprobe reads every stream in the file and this pass was keeping
            // only the subtitles, so a library whose files were renamed on the
            // way in had no codec and no audio layout at all — the release name
            // is the only other source and a renamed file has none. Written
            // here because the file is already open; a second pass to ask the
            // same question of the same bytes would be a second scheduler's
            // worth of work for nothing (DESIGN-002 rule 3).
            if (inventory.Probed is { } probed)
            {
                await mediaStateRepository.UpdateProbedFileFactsAsync(
                    kind,
                    candidate.MediaId,
                    candidate.FilePath,
                    new ProbedFileFacts(probed.VideoCodec, probed.AudioCodec, probed.AudioChannels),
                    cancellationToken);
            }

            scanned++;
            if (rows.Count > 0)
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
    /// Everything a file has, which is what a scan hands over — because
    /// <c>RecordScanAsync</c> replaces the lot and deletes anything not in it.
    ///
    /// <para><b>This is the destructive edge of the re-read cadence, so it is
    /// named rather than inlined.</b> A cheap re-read looks only at the folder,
    /// so it finds no embedded tracks — not because there are none, but because
    /// nobody looked. Handing that list over as the whole truth would delete
    /// every embedded subtitle in the library twelve hours after it was found,
    /// silently, and the bar would go red on files nothing was wrong with. The
    /// rig cannot catch it either: its videos were remuxed with <c>-sn</c> and
    /// have no embedded tracks at all.</para>
    ///
    /// <para>What was recorded goes first, so a sidecar in the same language
    /// wins the upsert behind it: a file you can swap or correct beats a track
    /// welded into the container.</para>
    /// </summary>
    /// <param name="videoWasProbed">
    /// Whether this pass actually read inside the container. When it did, what
    /// it found is the whole truth on its own.
    /// </param>
    public static IReadOnlyList<MediaSubtitleRow> WholeTruth(
        bool videoWasProbed,
        IReadOnlyList<MediaSubtitleRow> recorded,
        IReadOnlyList<MediaSubtitleRow> found)
        => videoWasProbed
            ? found
            : [.. recorded.Where(row => row.Source == SubtitleSources.Embedded), .. found];

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
            // Not "ffprobe is not installed" any more: Deluno ships it, so this
            // is a broken install rather than a missing prerequisite, and the
            // old wording sent people off to install what they already had.
            summary += " ffprobe is missing from this install, so subtitles inside the video files could not be read — only files beside them.";
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
