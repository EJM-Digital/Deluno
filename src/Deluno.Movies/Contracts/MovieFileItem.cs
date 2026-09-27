namespace Deluno.Movies.Contracts;

/// <summary>
/// One file Deluno found on disk for a movie (#782) — the tracked video, a
/// subtitle sidecar, or anything else sitting in the title's folder (an
/// extra, a sample, an nfo). The movie model has only ever carried one
/// filePath/fileSizeBytes; a title with a remux plus a sample had no way to
/// see the second file at all.
/// </summary>
public sealed record MovieFileItem(
    string Path,
    long? SizeBytes,
    /// <summary>video | subtitle | other</summary>
    string Kind,
    /// <summary>The file <c>movie_wanted_state.file_path</c> tracks.</summary>
    bool IsPrimary,
    string? Quality,
    string? VideoCodec,
    string? AudioCodec,
    string? AudioChannels,
    /// <summary>Normalized ISO 639-1, for a subtitle row. Null for anything else.</summary>
    string? Language,
    bool? HearingImpaired);
