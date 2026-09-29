namespace Deluno.Movies.Contracts;

public sealed record MovieSearchHistoryItem(
    string Id,
    string MovieId,
    string LibraryId,
    string TriggerKind,
    string Outcome,
    string? ReleaseName,
    string? IndexerName,
    string? DetailsJson,
    DateTimeOffset CreatedUtc,
    /// <summary>The indexer's id (#1077): what the search is filed under, so renaming the indexer keeps its history. Null when the source matched no indexer.</summary>
    string? IndexerId = null);
