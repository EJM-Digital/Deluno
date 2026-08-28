using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;

namespace Deluno.Media;

public sealed record MediaBulkSearchResult(
    IReadOnlyDictionary<string, string[]>? ValidationErrors,
    int SearchesTriggered,
    int LibraryCount);

/// <summary>
/// Queues one search cycle per library represented by an explicit selection.
/// The selected wanted rows are resolved by ID in SQL rather than through the
/// bounded recent-summary list.
/// </summary>
public static class MediaBulkSearchHandler
{
    public static async Task<MediaBulkSearchResult> ExecuteAsync(
        MediaKind kind,
        IReadOnlyList<string>? mediaIds,
        IMediaStateRepository mediaStateRepository,
        ILibrariesRepository librariesRepository,
        IJobQueueRepository jobQueueRepository,
        CancellationToken cancellationToken)
    {
        var entityName = kind == MediaKind.Movie ? "movie" : "series";
        var entityIdsName = kind == MediaKind.Movie ? "movieIds" : "seriesIds";
        if (mediaIds is not { Count: > 0 })
        {
            return new MediaBulkSearchResult(
                new Dictionary<string, string[]>
                {
                    [entityIdsName] = [$"Choose at least one {entityName} to search for."]
                },
                SearchesTriggered: 0,
                LibraryCount: 0);
        }

        var wantedItems = await mediaStateRepository.ListWantedByIdsAsync(
            kind,
            mediaIds,
            cancellationToken);
        var libraryIds = wantedItems
            .Where(item => !string.IsNullOrWhiteSpace(item.LibraryId))
            .Select(item => item.LibraryId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        var triggered = 0;
        foreach (var libraryId in libraryIds)
        {
            var library = libraries.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, libraryId, StringComparison.OrdinalIgnoreCase));
            if (library is null)
            {
                continue;
            }

            await jobQueueRepository.RequestLibrarySearchAsync(
                new LibraryAutomationPlanItem(
                    LibraryId: library.Id,
                    LibraryName: library.Name,
                    MediaType: library.MediaType,
                    AutoSearchEnabled: library.AutoSearchEnabled,
                    MissingSearchEnabled: library.MissingSearchEnabled,
                    UpgradeSearchEnabled: library.UpgradeSearchEnabled,
                    SearchIntervalHours: library.SearchIntervalHours,
                    RetryDelayHours: library.RetryDelayHours,
                    MaxItemsPerRun: library.MaxItemsPerRun,
                    SearchWindowStartHour: library.SearchWindowStartHour,
                    SearchWindowEndHour: library.SearchWindowEndHour),
                cancellationToken);
            triggered++;
        }

        return new MediaBulkSearchResult(
            ValidationErrors: null,
            SearchesTriggered: triggered,
            LibraryCount: libraryIds.Length);
    }
}
