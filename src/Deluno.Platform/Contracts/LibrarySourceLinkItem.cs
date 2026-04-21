namespace Deluno.Platform.Contracts;

public sealed record LibrarySourceLinkItem(
    string Id,
    string LibraryId,
    string IndexerId,
    string IndexerName,
    int Priority,
    string RequiredTags,
    string ExcludedTags,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);
