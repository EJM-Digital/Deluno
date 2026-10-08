namespace Deluno.Series.Contracts;

/// <summary>An episode that holds a file, with the show and library it belongs to (#1173).</summary>
public sealed record EpisodeFileLink(
    string EpisodeId,
    string SeriesId,
    string SeriesTitle,
    string LibraryId,
    int SeasonNumber,
    int EpisodeNumber,
    string FilePath);
