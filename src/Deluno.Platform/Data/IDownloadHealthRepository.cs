using Deluno.Contracts;
using Deluno.Platform.Contracts;

namespace Deluno.Platform.Data;

public interface IDownloadHealthRepository
{
    Task<IReadOnlyList<DownloadHealthRecord>> RecordDownloadHealthObservationsAsync(
        IReadOnlyList<DownloadHealthObservation> observations,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DownloadHealthRecord>> ListDownloadHealthRecordsAsync(
        int take,
        CancellationToken cancellationToken);

    Task<Page<DownloadHealthRecord>> ListDownloadHealthRecordsPageAsync(
        PageRequest request,
        CancellationToken cancellationToken);

    Task<bool> IsDownloadReleaseBlockedAsync(
        string clientId,
        string releaseName,
        CancellationToken cancellationToken);

    Task<DownloadHealthRecord?> IgnoreDownloadHealthFindingAsync(
        string clientId,
        string queueItemId,
        string kind,
        int durationDays,
        CancellationToken cancellationToken);
}
