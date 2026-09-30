using Deluno.Contracts;
using Deluno.Jobs.Contracts;

namespace Deluno.Jobs.Data;

public interface IDownloadDispatchesRepository
{
    /// <summary>
    /// Get a single dispatch by ID with all grab, detection, and import outcome data.
    /// </summary>
    Task<DownloadDispatchItem?> GetDispatchAsync(
        string dispatchId,
        CancellationToken cancellationToken);

    /// <summary>
    /// A dispatch whether or not it has been archived: a recovery case can outlive
    /// its download, and still has to say which title it was about (#558).
    /// </summary>
    Task<DownloadDispatchItem?> GetDispatchIncludingArchivedAsync(
        string dispatchId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Record a grab outcome (called after DownloadClientGrabService.GrabAsync).
    /// </summary>
    Task<DownloadDispatchItem> RecordGrabAsync(
        string dispatchId,
        string grabStatus,
        int? grabResponseCode,
        string? grabMessage,
        string? grabFailureCode,
        string? grabResponseJson,
        CancellationToken cancellationToken,
        IntegrationFailure? failure = null,
        string? externalId = null);

    /// <summary>
    /// Record that the download client has shown this download, from polling or
    /// its completion webhook (#554). The first sighting sets the detection time
    /// and undoes a "never shown" ending; <paramref name="completed"/> says the
    /// client has finished with it, which starts the wait for an import. Each is
    /// written once, however often the client reports the download.
    /// </summary>
    Task<DownloadDispatchItem> RecordDetectionAsync(
        string dispatchId,
        string? torrentHashOrItemId,
        long? downloadedBytes,
        bool completed,
        CancellationToken cancellationToken);

    /// <summary>
    /// Record import outcome (success or failure).
    /// </summary>
    Task<DownloadDispatchItem> RecordImportOutcomeAsync(
        string dispatchId,
        string importStatus,
        string? importedFilePath,
        string? importFailureCode,
        string? importFailureMessage,
        CancellationToken cancellationToken,
        IntegrationFailure? failure = null);

    /// <summary>
    /// Query dispatches with filtering and pagination.
    /// </summary>
    Task<(IReadOnlyList<DownloadDispatchItem> Items, string? NextPageToken)> QueryDispatchesAsync(
        DispatchQueryFilter filter,
        DispatchPaginationOptions pagination,
        CancellationToken cancellationToken);

    /// <summary>
    /// Find dispatches that were grabbed but not detected in client.
    /// </summary>
    Task<IReadOnlyList<DownloadDispatchItem>> FindUnresolvedDispatchesAsync(
        int minAgeMinutes,
        string? clientId,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// The downloads still on their way: sent to a client and not yet imported,
    /// refused at import, or archived, whether or not the client has shown them
    /// yet (#554, #555).
    /// </summary>
    Task<IReadOnlyList<DownloadInFlight>> ListDownloadsInFlightAsync(
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Get full timeline of events for a dispatch.
    /// </summary>
    Task<IReadOnlyList<DispatchTimelineEvent>> GetDispatchTimelineAsync(
        string dispatchId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Record a timeline event for a dispatch.
    /// </summary>
    Task<DispatchTimelineEvent> RecordTimelineEventAsync(
        string dispatchId,
        string eventType,
        string? detailsJson,
        CancellationToken cancellationToken);

    /// <summary>
    /// Update circuit breaker state for a client.
    /// </summary>
    Task<DownloadDispatchItem> SetCircuitBreakerAsync(
        string dispatchId,
        DateTimeOffset? openUntilUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Find a dispatch by torrent hash or item ID, scoped to a specific client.
    /// </summary>
    Task<DownloadDispatchItem?> FindDispatchByHashAsync(
        string clientId,
        string hash,
        CancellationToken cancellationToken);

    /// <summary>
    /// Like <see cref="FindDispatchByHashAsync"/> but counts archived dispatches
    /// too. An archived dispatch is still proof Deluno sent that download (#1062).
    /// </summary>
    Task<DownloadDispatchItem?> FindAnyDispatchByHashAsync(
        string clientId,
        string hash,
        CancellationToken cancellationToken);

    /// <summary>
    /// Which of these release names Deluno has a dispatch for, any status,
    /// archived included, whatever the number of dispatches (#1062). Names are
    /// matched ignoring case; the release names as recorded are returned.
    /// Asked by the leftover scan with the names it found on disk, so there is
    /// no recent-dispatches window to fall outside.
    /// </summary>
    Task<IReadOnlyList<string>> FindGrabbedReleaseNamesAsync(
        IReadOnlyCollection<string> candidateNames,
        CancellationToken cancellationToken);

    /// <summary>
    /// Every dispatch (archived included) on this client whose recorded
    /// hash/item id, or own id, is one of <paramref name="clientItemIds"/>,
    /// compared ignoring case. This is how ownership is decided for what a
    /// client reports: by asking about exactly those ids, so it never depends
    /// on how many newer dispatches the client has had since (#1062).
    /// </summary>
    Task<IReadOnlyList<DownloadDispatchItem>> FindDispatchesByClientItemIdsAsync(
        string clientId,
        IReadOnlyCollection<string> clientItemIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// The newest dispatch (archived or not) for this client and release name
    /// that never recorded the client's own id for it. Used to give such a
    /// dispatch the id once the client shows the item (#1062).
    /// </summary>
    Task<DownloadDispatchItem?> FindDispatchWithoutClientIdByReleaseNameAsync(
        string clientId,
        string releaseName,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records the client's own id on a dispatch that has none. Never
    /// overwrites an id that is already there.
    /// </summary>
    Task RecordClientItemIdAsync(
        string dispatchId,
        string clientItemId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Find the most recent grabbed-but-undetected dispatch matching a release name for a client.
    /// </summary>
    Task<DownloadDispatchItem?> FindDispatchByReleaseNameAsync(
        string clientId,
        string releaseName,
        CancellationToken cancellationToken);

    /// <summary>
    /// Find dispatches that have been in a failed state for longer than <paramref name="minAge"/>.
    /// </summary>
    Task<IReadOnlyList<DownloadDispatchItem>> FindStaleFailedDispatchesAsync(
        TimeSpan minAge,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Ends a download the client accepted and then never showed. Returns
    /// <c>true</c> when this call ended it, <c>false</c> when it had already
    /// ended, been detected or been archived.
    ///
    /// <para>It used to stay "sent" for ever: the poller raised an alert and an
    /// import issue about it and left the download itself alone, so it read as a
    /// green "Sent" on every screen, no screen offered anything to do with it,
    /// and nothing ever archived it.</para>
    /// </summary>
    Task<bool> MarkNeverDetectedAsync(string dispatchId, string message, CancellationToken cancellationToken);

    /// <summary>
    /// Archive/delete a dispatch (soft delete).
    /// </summary>
    Task ArchiveDispatchAsync(
        string dispatchId,
        string reason,
        CancellationToken cancellationToken);

    /// <summary>
    /// Find old, unresolved dispatches eligible for cleanup/archival.
    /// </summary>
    Task<IReadOnlyList<DownloadDispatchItem>> FindOldUnresolvedDispatchesAsync(
        TimeSpan minAge,
        int limit,
        CancellationToken cancellationToken);
}

/// <summary>
/// What a download's history says happened to it (#1022), in Radarr's names for
/// the same events. One per download: the furthest it got.
/// </summary>
public static class DispatchHistoryEvents
{
    /// <summary>The client took it and it has not been imported or failed yet.</summary>
    public const string Grabbed = "grabbed";

    /// <summary>The finished download was imported into the library.</summary>
    public const string Imported = "downloadFolderImported";

    /// <summary>The grab was refused, or the finished download could not be imported.</summary>
    public const string Failed = "downloadFailed";

    public static bool IsKnown(string? value) => Normalize(value) is not null;

    public static string? Normalize(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "grabbed" => Grabbed,
            "downloadfolderimported" or "imported" => Imported,
            "downloadfailed" or "failed" => Failed,
            _ => null
        };

    /// <summary>The event a dispatch row is in, by the same rule the filter uses.</summary>
    public static string For(string? grabStatus, string? importStatus)
        => string.Equals(importStatus, "imported", StringComparison.OrdinalIgnoreCase) ? Imported
            : string.Equals(grabStatus, "failed", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(importStatus, "failed", StringComparison.OrdinalIgnoreCase) ? Failed
            : Grabbed;
}

public class DispatchQueryFilter
{
    /// <summary>One of <see cref="DispatchHistoryEvents"/>; anything else matches nothing extra.</summary>
    public string? HistoryEvent { get; set; }
    public string? Status { get; set; }
    public string? GrabStatus { get; set; }
    public string? ImportStatus { get; set; }
    public string? ClientId { get; set; }
    public string? MediaType { get; set; }
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string? LibraryId { get; set; }
    public DateTimeOffset? MinGrabTime { get; set; }
    public DateTimeOffset? MaxGrabTime { get; set; }
    public DateTimeOffset? MinImportTime { get; set; }
    public DateTimeOffset? MaxImportTime { get; set; }

    /// <summary>
    /// Include rows that have been archived. A successful import archives its
    /// dispatch at once, so the operational views that hide archived rows are
    /// right and a history view that hides them shows an empty past.
    /// </summary>
    public bool IncludeArchived { get; set; }
}

public class DispatchPaginationOptions
{
    public int PageSize { get; set; } = 50;
    public string? PageToken { get; set; }
}
