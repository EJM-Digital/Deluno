using Deluno.Contracts;

namespace Deluno.Media;

/// <summary>
/// What a grab the download client took does to its title.
/// </summary>
public static class GrabbedTitles
{
    /// <summary>The grab status a download client gives a release it accepted.</summary>
    public const string Sent = "sent";

    /// <summary>
    /// A release the client took puts its title on its way: the shelf stops
    /// saying "go and get this", and the search cycle stops looking for it, which
    /// is what stops the same release being grabbed twice.
    /// <see cref="DownloadStateReconciler"/> puts the title back when the
    /// download ends without an import.
    ///
    /// <para>Manual grabs did this and automatic ones did not, so an automatic
    /// grab left its title on the work list, and once its retry window passed the
    /// same release was grabbed again. On the rig one TV episode was downloaded
    /// five times over two days while the first copy waited with Weir (#555).
    /// One step for every grab path, so none of them can leave it out.</para>
    /// </summary>
    public static Task MarkDownloadingIfSentAsync(
        this IMediaStateRepository mediaState,
        MediaKind kind,
        string mediaId,
        string grabStatus,
        DateTimeOffset now,
        CancellationToken cancellationToken)
        => grabStatus == Sent
            ? mediaState.SetDownloadingAsync(kind, mediaId, downloading: true, now, cancellationToken)
            : Task.CompletedTask;
}
