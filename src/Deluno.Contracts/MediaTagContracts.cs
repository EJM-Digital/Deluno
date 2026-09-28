namespace Deluno.Contracts;

/// <summary>
/// A user-owned label attached to one catalogue entry. The tag definition
/// lives in the platform database; the assignment lives beside the catalogue
/// entry so it can be filtered without crossing database boundaries.
/// </summary>
public sealed record MediaTagAssignment(string TagId, string Name);

/// <summary>One title with the tag names it carries (none for an untagged title), for checks that need every title (#1043).</summary>
public sealed record MediaTitleTags(string Id, string Title, bool IsMonitored, IReadOnlyList<string> Tags);

/// <summary>One managed tag's usage count in one catalogue.</summary>
public sealed record MediaTagUsage(string Name, int TitleCount);

/// <summary>
/// The shared tag persistence boundary used by both catalogue engines and the
/// platform's usage/deletion endpoints. Implementations select the database
/// through the closed <see cref="MediaKind"/> map; callers never provide table
/// names or SQL fragments.
/// </summary>
public interface IMediaTagStore
{
    Task<IReadOnlyList<MediaTagAssignment>> ListAsync(
        MediaKind kind,
        string mediaId,
        CancellationToken cancellationToken);

    /// <summary>
    /// The tags of the show an episode belongs to (tags live on the show, not
    /// the episode), or null when no such episode exists (#1036).
    /// </summary>
    Task<IReadOnlyList<MediaTagAssignment>?> ListForEpisodeAsync(
        string episodeId,
        CancellationToken cancellationToken);

    Task ReplaceAsync(
        MediaKind kind,
        string mediaId,
        IReadOnlyList<MediaTagAssignment> assignments,
        CancellationToken cancellationToken);

    /// <summary>Every title in one catalogue with its tag names, untagged titles included (#1043).</summary>
    Task<IReadOnlyList<MediaTitleTags>> ListAllTitleTagsAsync(
        MediaKind kind,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MediaTagUsage>> ListUsageAsync(
        MediaKind kind,
        CancellationToken cancellationToken);

    Task RenameAsync(
        MediaKind kind,
        string tagId,
        string previousName,
        string nextName,
        CancellationToken cancellationToken);

    Task RemoveAsync(
        MediaKind kind,
        string tagId,
        string name,
        CancellationToken cancellationToken);
}

/// <summary>
/// Gives tags to a title that a source (a Seerr connection, a collection, the
/// "no longer on any list" setting) just added or found (#1046). A tag name
/// nobody has defined yet is defined on the spot, so a source never fails
/// because of a tag; a title keeps the tags it already had.
/// </summary>
public interface ITitleTagService
{
    /// <summary>The tag names the title carries now (empty when it has none or does not exist).</summary>
    Task<IReadOnlyList<string>> ListNamesAsync(MediaKind kind, string mediaId, CancellationToken cancellationToken);

    /// <summary>Adds the tags to the title and returns the names it did not already carry.</summary>
    Task<IReadOnlyList<string>> GiveAsync(MediaKind kind, string mediaId, IEnumerable<string> tagNames, CancellationToken cancellationToken);
}

public static class TagNameList
{
    /// <summary>Splits a comma-separated tag list into distinct trimmed names, keeping the first spelling.</summary>
    public static IReadOnlyList<string> Split(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? []
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
}

/// <summary>
/// One place that turns "the title this event is about" into tag names for
/// notification dispatch (#1036). Null means the entity is not a title Deluno
/// can resolve (so the event is treated as title-less); an empty list means a
/// real title with no tags.
/// </summary>
public static class MediaTagStoreExtensions
{
    public static async Task<IReadOnlyList<string>?> ListTitleTagNamesAsync(
        this IMediaTagStore? store,
        string? entityType,
        string? entityId,
        CancellationToken cancellationToken)
    {
        if (store is null || string.IsNullOrWhiteSpace(entityId))
        {
            return null;
        }

        IReadOnlyList<MediaTagAssignment>? assignments = entityType?.Trim().ToLowerInvariant() switch
        {
            "movie" or "movies" => await store.ListAsync(MediaKind.Movie, entityId, cancellationToken),
            "series" or "tv" => await store.ListAsync(MediaKind.Series, entityId, cancellationToken),
            "episode" => await store.ListForEpisodeAsync(entityId, cancellationToken),
            _ => null
        };
        return assignments?.Select(assignment => assignment.Name).ToArray();
    }
}
