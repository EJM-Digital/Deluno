namespace Deluno.Series.Contracts;

/// <summary>
/// A file-derived TV number that is not necessarily the canonical catalogue
/// season/episode key. The series repository resolves it against persisted
/// provider/owner mappings and refuses zero or multiple matches.
/// </summary>
public sealed record ImportedEpisodeNumberingItem(
    string NumberingScheme,
    int? SeasonNumber = null,
    int? EpisodeNumber = null,
    int? AbsoluteNumber = null,
    int? SceneSeasonNumber = null,
    int? SceneEpisodeNumber = null,
    DateOnly? AirDate = null,
    bool HasFile = true,
    string? FilePath = null,
    long? FileSizeBytes = null);
