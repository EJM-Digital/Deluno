namespace Deluno.Series.Contracts;

public sealed record ImportedEpisodeItem(
    int SeasonNumber,
    int EpisodeNumber,
    bool HasFile);
