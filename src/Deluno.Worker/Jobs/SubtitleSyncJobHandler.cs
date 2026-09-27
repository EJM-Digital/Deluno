using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Media;

namespace Deluno.Worker.Jobs;

/// <summary>
/// Puts one fetched subtitle in time with its video.
///
/// <para><b>Its own job, and its own lane, deliberately.</b> The obvious place
/// to do this was inside the fetch, immediately after the file lands — it is one
/// call and the path is right there. That would have been the fourth time this
/// project made the same mistake: a subtitle sync takes seconds of FFmpeg
/// against a file on disk, and running it inside the fetch would park a
/// <c>subtitles.search</c> slot on local work while a provider's daily allowance
/// went unspent. The owner: <i>"nothing should be shared or have to wait for another
/// process or anything."</i> Fetching talks to strangers; timing reads a disk.
/// They are different kinds of work and they get different lanes.</para>
///
/// <para><b>Which subtitles get here.</b> Only the ones the shelf is still
/// calling upgradable — below <see cref="SubtitleCutoff.Rung"/>. A subtitle that
/// names your exact release group was cut against this encode and is in time by
/// construction; there is nothing to fix and every reason not to touch it. Bazarr
/// arrives at the same place from the other direction, syncing what scores under
/// a threshold and asking you to choose the threshold. Deluno defaults to its
/// named cutoff, while a library may narrow or disable that behaviour; the
/// queued payload carries the choice.</para>
///
/// <para><b>Doing nothing is the common outcome and is reported as one.</b> Most
/// subtitles are already in time. The job says which of the several reasons
/// applied, because "sync ran and changed nothing" and "sync never ran" look
/// identical from the outside, and this codebase has already shipped that
/// confusion once — a library asked for English every day and never got it, in
/// silence.</para>
///
/// <para><b>#897 and #922 added a fifth outcome.</b> Not every "did nothing"
/// is the same "did nothing": a confident mismatch is rejected, deleted,
/// blocklisted and refetched (#897), and an inconclusive one is kept and
/// marked "Timing: not checked" (#922) rather than looking identical to a
/// subtitle nobody has ever asked about.</para>
/// </summary>
public sealed class SubtitleSyncJobHandler(
    ISubtitleTimingSync timingSync,
    IMediaSubtitleRepository mediaSubtitleRepository,
    IActivityFeedRepository activityFeedRepository)
    : IJobHandler
{
    public string JobType => "subtitle.sync";

    public async Task<string> HandleAsync(JobQueueItem job, CancellationToken cancellationToken)
    {
        var payload = ParsePayload(job.PayloadJson);
        if (payload is null || string.IsNullOrWhiteSpace(payload.VideoPath) || string.IsNullOrWhiteSpace(payload.SubtitlePath))
        {
            return "This timing job does not say which subtitle it is for, so nothing was done.";
        }

        var result = await timingSync.SyncAsync(
            payload.VideoPath,
            payload.SubtitlePath,
            payload.OriginalLanguage,
            cancellationToken,
            payload.Policy);

        // Identity is optional so an old queued job (from before #897/#922)
        // still runs the sync itself; it just cannot record or act on the
        // outcome against a media row it was never told about.
        if (payload.Identity is { } identity)
        {
            if (result.Outcome == SubtitleTimingOutcome.Mismatched)
            {
                await SubtitleRejection.RejectAsync(
                    mediaSubtitleRepository,
                    activityFeedRepository,
                    identity.Kind,
                    identity.MediaId,
                    identity.Title,
                    identity.Language,
                    forced: false,
                    hearingImpaired: identity.HearingImpaired,
                    payload.SubtitlePath,
                    identity.SourceKey,
                    identity.ProviderKey,
                    releaseName: null,
                    result.Reason,
                    cancellationToken);
            }
            else
            {
                await mediaSubtitleRepository.RecordTimingStatusAsync(
                    identity.Kind,
                    identity.MediaId,
                    identity.Language,
                    forced: false,
                    hearingImpaired: identity.HearingImpaired,
                    StoredTimingStatus(result.Outcome),
                    result.Reason,
                    cancellationToken);
            }
        }

        // The path came out of a stored job payload, not off this filesystem,
        // so it is read by its shape. A job queued on Windows and run by the
        // container otherwise reports "D:\Mediailm.en.srt" as the name.
        var name = MediaPath.FileName(payload.SubtitlePath);
        return $"{name}: {result.Reason}";
    }

    private static string StoredTimingStatus(SubtitleTimingOutcome outcome)
        => outcome switch
        {
            SubtitleTimingOutcome.Moved => "moved",
            SubtitleTimingOutcome.AlreadyInTime => "already_in_time",
            _ => "not_checked"
        };

    private static SubtitleSyncPayload? ParsePayload(string? payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<SubtitleSyncPayload>(payloadJson ?? "{}", JobPayloads.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Which title and language this file is for, and the exact candidate it
    /// was fetched as — everything #897's automatic reject and #922's
    /// "Timing: not checked" mark need that a path alone cannot say.
    /// </summary>
    public sealed record SubtitleSyncIdentity(
        MediaKind Kind,
        string MediaId,
        string Title,
        string Language,
        bool HearingImpaired,
        string? ProviderKey,
        string? SourceKey);

    /// <summary>
    /// Paths, plus — since #897 and #922 — the identity a rejection or a
    /// timing mark needs to act on. The paths remain primary because the
    /// enqueuing code has them already from writing the file, and a path that
    /// has since moved is handled where it should be: the sync service checks
    /// the file is there and says so if it is not.
    /// </summary>
    public sealed record SubtitleSyncPayload(
        string VideoPath,
        string SubtitlePath,
        string? OriginalLanguage,
        SubtitleTimingPolicy? Policy = null,
        SubtitleSyncIdentity? Identity = null);
}
