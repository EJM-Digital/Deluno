using Deluno.Contracts;
using Deluno.Jobs.Data;

namespace Deluno.Media;

/// <summary>
/// Rejects one subtitle Deluno already fetched: deletes the file, blocklists
/// it so it is never offered again, drops the held row so the language goes
/// back to being searched for, and says so in Activity in plain words.
///
/// <para><b>One place, two callers.</b> #897's automatic reject-on-mismatch
/// and #922's one-click "This is out of sync" button are the same action
/// with a different trigger — a person deciding by eye what the correlation
/// could not decide for itself. Writing it twice is exactly the drift this
/// codebase keeps finding after the fact, so both go through here.</para>
/// </summary>
public static class SubtitleRejection
{
    public static async Task RejectAsync(
        IMediaSubtitleRepository subtitles,
        IActivityFeedRepository activity,
        MediaKind kind,
        string mediaId,
        string title,
        string language,
        bool forced,
        bool hearingImpaired,
        string filePath,
        string? blockKey,
        string? providerKey,
        string? releaseName,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (IOException)
        {
            // A file the OS would not delete right now is not a reason to skip
            // the rest of the rejection - it is blocklisted and stops counting
            // as held either way, and a stray file on disk is the smaller
            // problem than offering the same bad subtitle again forever.
        }
        catch (UnauthorizedAccessException)
        {
        }

        if (!string.IsNullOrWhiteSpace(blockKey))
        {
            await subtitles.BlockSubtitleAsync(
                kind, mediaId, language, blockKey, providerKey, releaseName, reason, cancellationToken);
        }

        await subtitles.RemoveSubtitleAsync(kind, mediaId, language, forced, hearingImpaired, cancellationToken);

        await activity.RecordActivityAsync(
            "subtitle.rejected",
            $"Rejected the {language} subtitle for {title}: {reason} It will not be fetched again; the next search tries the next candidate.",
            null,
            null,
            kind == MediaKind.Series ? "series" : "movie",
            mediaId,
            cancellationToken);
    }
}
