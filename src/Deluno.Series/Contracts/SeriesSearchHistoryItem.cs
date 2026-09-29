namespace Deluno.Series.Contracts;

public sealed record SeriesSearchHistoryItem(
    string Id,
    string SeriesId,
    string? EpisodeId,
    int? SeasonNumber,
    int? EpisodeNumber,
    string LibraryId,
    string TriggerKind,
    string Outcome,
    string? ReleaseName,
    string? IndexerName,
    string? DetailsJson,
    DateTimeOffset CreatedUtc,
    /// <summary>The indexer's id (#1077): what the search is filed under, so renaming the indexer keeps its history. Null when the source matched no indexer.</summary>
    string? IndexerId = null);
