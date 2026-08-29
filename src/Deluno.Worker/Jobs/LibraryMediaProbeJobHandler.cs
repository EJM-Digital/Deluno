using Deluno.Quality;
using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Media;
using Microsoft.Extensions.Logging;

namespace Deluno.Worker.Jobs;

/// <summary>
/// Reads what a file actually is — codec, audio, channel layout — and records
/// it against the copy you hold.
///
/// <para><b>Why it is its own pass.</b> These facts were briefly read inside
/// the subtitle scan, because that pass already runs ffprobe and opening the
/// file twice looked wasteful. James: <i>"dont you think its better we separate
/// these jobs so nothing relies on each other or fights or conflicts or
/// overlaps... everything needs to run independently"</i>. He is right, and the
/// coupling had already produced a defect: the subtitle scan returns
/// immediately for a library asking for no subtitle languages, so turning
/// subtitles off would have silently stopped codecs ever being read. One saved
/// file read is not worth a feature that only works while an unrelated one is
/// switched on.</para>
///
/// <para><b>What it is for.</b> The codec, the audio and the channel layout are
/// otherwise parsed from the release name, which carries them by convention and
/// carries nothing once a library has been renamed on the way in. On the rig,
/// <c>Big Buck Bunny (2008).mkv</c> yields none of the three and the Codec
/// switch draws a dash on every card.</para>
///
/// <para><b>Bounded like the scans beside it.</b> One slice per job, re-queued
/// while there is more to do, so it drains at the lane's own pace rather than
/// holding a lease over a library.</para>
/// </summary>
public sealed class LibraryMediaProbeJobHandler(
    IMediaStateRepository mediaStateRepository,
    IMediaProbeService mediaProbeService,
    IJobScheduler jobScheduler,
    ILogger<LibraryMediaProbeJobHandler> logger)
    : IJobHandler
{
    /// <summary>
    /// How many files one slice reads.
    ///
    /// <para>Local disk and a local process, so this is sized like the subtitle
    /// scan rather than like an indexer call — the thing being protected is the
    /// lane's own concurrency, not somebody else's server.</para>
    /// </summary>
    private const int SliceSize = 40;

    public string JobType => "library.media.probe";

    public async Task<string> HandleAsync(JobQueueItem job, CancellationToken cancellationToken)
    {
        var kind = job.RelatedEntityType == "series" ? MediaKind.Series : MediaKind.Movie;

        var candidates = await mediaStateRepository.ListFileProbeCandidatesAsync(kind, SliceSize, cancellationToken);
        if (candidates.Count == 0)
        {
            return "Every file Deluno holds has been read.";
        }

        var read = 0;
        var unreadable = 0;

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var probe = await mediaProbeService.ProbeAsync(candidate.FilePath, cancellationToken);

            // A file ffprobe cannot read is still recorded, with nothing in it.
            // The write stamps the bookkeeping either way, so an unreadable
            // file is not retried at the front of every future pass — and the
            // COALESCE means a failed read never erases what the release name
            // already said.
            var facts = probe.Status == "succeeded"
                ? new ProbedFileFacts(
                    MediaProbedFacts.VideoCodec(probe.VideoStreams.FirstOrDefault()?.Codec),
                    MediaProbedFacts.AudioCodec(
                        probe.AudioStreams.FirstOrDefault()?.Codec,
                        probe.AudioStreams.FirstOrDefault()?.Profile),
                    MediaProbedFacts.AudioChannels(
                        probe.AudioStreams.FirstOrDefault()?.ChannelLayout,
                        probe.AudioStreams.FirstOrDefault()?.Channels))
                : new ProbedFileFacts(null, null, null);

            if (probe.Status == "succeeded")
            {
                read++;
            }
            else
            {
                unreadable++;
            }

            await mediaStateRepository.UpdateProbedFileFactsAsync(
                kind,
                candidate.MediaId,
                candidate.FilePath,
                facts,
                cancellationToken);
        }

        // More to do, so the next slice queues itself. Same shape as the
        // subtitle scan's own slicing, and deliberately not the same job.
        if (candidates.Count == SliceSize)
        {
            await jobScheduler.EnqueueAsync(
                new EnqueueJobRequest(
                    JobType: JobType,
                    Source: "system",
                    PayloadJson: null,
                    RelatedEntityType: job.RelatedEntityType,
                    RelatedEntityId: null,
                    // One queued slice at a time per kind: without this a pass
                    // that starts while the previous one is still draining
                    // stacks a second chain of slices behind the first.
                    DedupeKey: $"media-probe:{job.RelatedEntityType}"),
                cancellationToken);
        }

        if (unreadable > 0)
        {
            logger.LogDebug(
                "Media probe read {Read} files and could not read {Unreadable}.",
                read,
                unreadable);
        }

        return unreadable == 0
            ? $"Read {read} file{(read == 1 ? "" : "s")}."
            : $"Read {read} of {candidates.Count} files; {unreadable} could not be read.";
    }
}
