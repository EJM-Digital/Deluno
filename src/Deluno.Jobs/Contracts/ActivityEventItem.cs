namespace Deluno.Jobs.Contracts;

public sealed record ActivityEventItem(
    string Id,
    string Category,
    string Message,
    string? DetailsJson,
    string? RelatedJobId,
    string? RelatedEntityType,
    string? RelatedEntityId,
    DateTimeOffset CreatedUtc)
{
    /// <summary>The event kind in plain words (#1086); screens show this, never <see cref="Category"/>.</summary>
    public string Label => Deluno.Contracts.ActivityEventLabels.For(Category);
}
