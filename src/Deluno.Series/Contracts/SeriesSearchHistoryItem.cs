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
    DateTimeOffset CreatedUtc);
