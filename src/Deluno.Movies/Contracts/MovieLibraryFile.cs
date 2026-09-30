namespace Deluno.Movies.Contracts;

/// <summary>One library's own tracked file for a film (#1065), so the Files tab can show every copy.</summary>
public sealed record MovieLibraryFile(
    string LibraryId,
    string FilePath,
    long? FileSizeBytes,
    string? CurrentQuality,
    string? VideoCodec,
    string? AudioCodec,
    string? AudioChannels);
