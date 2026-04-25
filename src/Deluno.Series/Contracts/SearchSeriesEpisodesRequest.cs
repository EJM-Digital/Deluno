namespace Deluno.Series.Contracts;

public sealed record SearchSeriesEpisodesRequest(
    IReadOnlyList<string> EpisodeIds);
