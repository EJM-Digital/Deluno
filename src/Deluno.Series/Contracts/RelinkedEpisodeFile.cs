namespace Deluno.Series.Contracts;

/// <summary>An episode that showed no file although its own, correctly named file sat in its show's folder, and now shows it (#1174).</summary>
public sealed record RelinkedEpisodeFile(
    string SeriesId,
    string SeriesTitle,
    string EpisodeId,
    int SeasonNumber,
    int EpisodeNumber,
    string FilePath);
