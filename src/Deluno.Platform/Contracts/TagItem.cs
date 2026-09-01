namespace Deluno.Platform.Contracts;

public sealed record TagItem(
    string Id,
    string Name,
    string Color,
    string Description,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

/// <summary>
/// Usage shown before a managed tag is renamed or removed. Counts are title
/// counts, not assignment-row counts, because a title carrying the same legacy
/// label and managed id must still be presented as one title.
/// </summary>
public sealed record TagUsageItem(
    string Id,
    string Name,
    int MovieCount,
    int SeriesCount)
{
    public int TotalCount => MovieCount + SeriesCount;
}
