namespace Deluno.Jobs.Contracts;

public sealed record EpisodeSearchPlanItem(
    string EpisodeId,
    string SeriesId,
    int SeasonNumber,
    int EpisodeNumber,
    string Title);
