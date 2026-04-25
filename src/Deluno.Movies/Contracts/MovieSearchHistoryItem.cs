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
    DateTimeOffset CreatedUtc);
