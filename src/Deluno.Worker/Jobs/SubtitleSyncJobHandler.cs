using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;

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
/// went unspent. James: <i>"nothing should be shared or have to wait for another
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
/// </summary>
public sealed class SubtitleSyncJobHandler(ISubtitleTimingSync timingSync) : IJobHandler
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

        // The path came out of a stored job payload, not off this filesystem,
        // so it is read by its shape. A job queued on Windows and run by the
        // container otherwise reports "D:\Mediailm.en.srt" as the name.
        var name = MediaPath.FileName(payload.SubtitlePath);
        return $"{name}: {result.Reason}";
    }

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
    /// Paths rather than identifiers.
    ///
    /// <para>The alternative — a media id the handler resolves back to a file —
    /// would be one more place that knows how to turn a catalogue row into a
    /// path, and the enqueuing code has the path already because it just wrote
    /// the file. A path that has moved by the time this runs is handled where it
    /// should be: the service checks the file is there and says so if it is
    /// not.</para>
    /// </summary>
    public sealed record SubtitleSyncPayload(
        string VideoPath,
        string SubtitlePath,
        string? OriginalLanguage,
        SubtitleTimingPolicy? Policy = null);
}
