namespace Deluno.Series.Contracts;

public sealed record ImportedEpisodeItem(
    int SeasonNumber,
    int EpisodeNumber,
    bool HasFile,
    string? FilePath = null,
    long? FileSizeBytes = null,
    int? AbsoluteNumber = null,
    DateOnly? AirDate = null,
    int? SceneSeasonNumber = null,
    int? SceneEpisodeNumber = null,
    string? NumberingSource = null);
