namespace Deluno.Contracts;

/// <summary>
/// A user-owned label attached to one catalogue entry. The tag definition
/// lives in the platform database; the assignment lives beside the catalogue
/// entry so it can be filtered without crossing database boundaries.
/// </summary>
public sealed record MediaTagAssignment(string TagId, string Name);

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

    Task ReplaceAsync(
        MediaKind kind,
        string mediaId,
        IReadOnlyList<MediaTagAssignment> assignments,
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
