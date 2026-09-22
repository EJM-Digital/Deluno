using Deluno.Contracts;
using Deluno.Platform.Contracts;

namespace Deluno.Platform.Data;

public interface IDownloadHealthRepository
{
    /// <summary>
    /// Records what one client's queue shows now.
    ///
    /// <para><paramref name="listedQueueItemIds"/> is every download the client
    /// listed in this snapshot, with or without a finding. A download listed
    /// without a finding it used to have has recovered from it, so that record
    /// ends. A download that is not listed at all has left the client, or the
    /// client did not answer, so its records are left alone (#560).</para>
    /// </summary>
    Task<IReadOnlyList<DownloadHealthRecord>> RecordDownloadHealthObservationsAsync(
        string clientId,
        IReadOnlyCollection<string> listedQueueItemIds,
        IReadOnlyList<DownloadHealthObservation> observations,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DownloadHealthRecord>> ListDownloadHealthRecordsAsync(
        int take,
        CancellationToken cancellationToken);

    Task<Page<DownloadHealthRecord>> ListDownloadHealthRecordsPageAsync(
        PageRequest request,
        CancellationToken cancellationToken);

    Task<DownloadHealthRecord?> IgnoreDownloadHealthFindingAsync(
        string clientId,
        string queueItemId,
        string kind,
        int durationDays,
        CancellationToken cancellationToken);
}
