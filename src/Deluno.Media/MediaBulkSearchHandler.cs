using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;

namespace Deluno.Media;

public sealed record MediaBulkSearchResult(
    IReadOnlyDictionary<string, string[]>? ValidationErrors,
    int SearchesTriggered,
    int LibraryCount);

/// <summary>
/// The job kinds and payload of a search for one chosen title, queued by a
/// bulk search and run by the worker's title-search handlers.
/// </summary>
public static class TitleSearchJobs
{
    public const string Movie = "movies.search.manual";
    public const string Series = "series.search.manual";

    public static string JobTypeFor(MediaKind kind) => kind == MediaKind.Movie ? Movie : Series;

    /// <summary>Said in Activity beside the result, so a search says what started it.</summary>
    public const string FromBulkSearch = "the Search button on the library";
    public const string FromBulkEditor = "the bulk editor";

    public static string Payload(string id, string title, string? libraryId, string startedBy)
        => JsonSerializer.Serialize(new { id, title, libraryId, startedBy });

    /// <summary>One per title and library, so asking twice does not search twice.</summary>
    public static string DedupeKey(MediaKind kind, string id, string? libraryId)
        => $"bulk-search:{JobTypeFor(kind)}:{id}:{libraryId}";
}

/// <summary>
/// Queues one search for each chosen title (and the library it is wanted in).
/// The selected wanted rows are resolved by ID in SQL rather than through the
/// bounded recent-summary list. Only the chosen titles are searched: nothing
/// flags a whole library, so the rest of it is left alone.
/// </summary>
public static class MediaBulkSearchHandler
{
    public static async Task<MediaBulkSearchResult> ExecuteAsync(
        MediaKind kind,
        IReadOnlyList<string>? mediaIds,
        IMediaStateRepository mediaStateRepository,
        IJobScheduler jobScheduler,
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

        var wantedItems = (await mediaStateRepository.ListWantedByIdsAsync(kind, mediaIds, cancellationToken))
            .Where(item => !string.IsNullOrWhiteSpace(item.LibraryId))
            .ToArray();
        foreach (var item in wantedItems)
        {
            await jobScheduler.EnqueueAsync(
                new EnqueueJobRequest(
                    JobType: TitleSearchJobs.JobTypeFor(kind),
                    Source: "bulk",
                    PayloadJson: TitleSearchJobs.Payload(item.Id, item.Title, item.LibraryId, TitleSearchJobs.FromBulkSearch),
                    RelatedEntityType: entityName,
                    RelatedEntityId: item.Id,
                    DedupeKey: TitleSearchJobs.DedupeKey(kind, item.Id, item.LibraryId)),
                cancellationToken);
        }

        return new MediaBulkSearchResult(
            ValidationErrors: null,
            SearchesTriggered: wantedItems.Length,
            LibraryCount: wantedItems.Select(item => item.LibraryId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
