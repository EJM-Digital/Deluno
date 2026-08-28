using Deluno.Contracts;
using Microsoft.Extensions.Logging;

namespace Deluno.Media;

/// <summary>
/// Puts a title back on the work list when the download it was waiting on is no
/// longer happening.
/// </summary>
public interface IDownloadStateReconciler
{
    Task<int> ReconcileAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The other half of <c>WantedStatuses.Downloading</c>.
///
/// <para><b>Why it has to exist.</b> Keeping a downloading title off the work
/// list is what stops Deluno grabbing the same release twice. It is also the way
/// the feature could quietly ruin a library: if a dispatch fails, is removed
/// from the client, or is lost when the process dies mid-flight, and nothing
/// rewrites the status, the title is never searched again — with no error, no
/// log line, and nothing on screen. The only symptom is an absence, which is the
/// hardest kind of defect this codebase has had to find twice already.</para>
///
/// <para>A successful import clears the status on its own, because the import
/// rewrites the wanted row. Everything else is this.</para>
///
/// <para><b>Why it is here rather than in the polling service.</b> The obvious
/// home is <c>DownloadDispatchPollingService</c>, which already watches
/// dispatches reach their terminal states — but that lives in
/// <c>Deluno.Jobs</c>, and <c>Deluno.Media</c> references <i>it</i>. The
/// dependency runs one way, so the reconciliation lives on the side that can see
/// both.</para>
///
/// <para><b>It is a set difference, not a walk.</b> Two bounded reads — the
/// titles claiming to download, and the dispatches still unresolved — and the
/// difference between them. Both sets are small on any real library, because a
/// download in flight is a rare state.</para>
/// </summary>
public sealed class DownloadStateReconciler(
    IMediaStateRepository mediaStateRepository,
    ILiveDownloadLookup liveDownloads,
    TimeProvider timeProvider,
    ILogger<DownloadStateReconciler> logger)
    : IDownloadStateReconciler
{
    /// <summary>
    /// How long a title is left alone after being marked.
    ///
    /// <para>A grace period rather than a timeout. A grab writes the wanted
    /// status and the dispatch row in that order, so a title read in the instant
    /// between the two looks abandoned when it is perfectly healthy. Ten minutes
    /// is far longer than that gap can ever be and far shorter than the
    /// seven-day backstop it sits in front of.</para>
    /// </summary>
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);

    public async Task<int> ReconcileAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        var stillGoing = (await liveDownloads.ListEntityIdsStillDownloadingAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        var cleared = 0;

        foreach (var kind in new[] { MediaKind.Movie, MediaKind.Series })
        {
            var claiming = await mediaStateRepository.ListDownloadingAsync(kind, now - Grace, cancellationToken);

            foreach (var mediaId in claiming.Where(id => !stillGoing.Contains(id)))
            {
                cancellationToken.ThrowIfCancellationRequested();

                await mediaStateRepository.SetDownloadingAsync(
                    kind, mediaId, downloading: false, now, cancellationToken);

                cleared++;
            }
        }

        if (cleared > 0)
        {
            // Worth a line, because the honest reading of it is "something went
            // wrong with a download and nobody told us". A steady trickle here
            // is a download client that is dropping work.
            logger.LogInformation(
                "Put {Count} title(s) back on the work list: they were waiting on a download that is no longer happening.",
                cleared);
        }

        return cleared;
    }
}
