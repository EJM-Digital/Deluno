namespace Deluno.Series.Contracts;

/// <summary>
/// One wrong episode-to-file link the repair (#1174) cleared: the file belongs to
/// the episode(s) its name gives, not to <see cref="ClearedSeasonNumber"/> x <see cref="ClearedEpisodeNumber"/>.
/// When the show's folder held that episode's own file (by SxxEyy in its name), it was linked
/// instead and <see cref="RelinkedFilePath"/> says which; otherwise the episode is Missing again.
/// </summary>
public sealed record WrongEpisodeFileLinkRepair(
    string SeriesId,
    string SeriesTitle,
    string EpisodeId,
    int ClearedSeasonNumber,
    int ClearedEpisodeNumber,
    string FilePath,
    string KeptEpisodes,
    string? RelinkedFilePath = null);
