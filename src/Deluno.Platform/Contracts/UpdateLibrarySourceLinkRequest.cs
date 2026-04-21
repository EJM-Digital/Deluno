namespace Deluno.Platform.Contracts;

public sealed record UpdateLibrarySourceLinkRequest(
    string IndexerId,
    int? Priority,
    string? RequiredTags,
    string? ExcludedTags);
