namespace Deluno.Series.Contracts;

/// <summary>One tracked show or episode file to mark gone in a batch (#1070).</summary>
public sealed record SeriesFileGoneRequest(string SeriesId, string? EpisodeId, string LibraryId, string FilePath);
