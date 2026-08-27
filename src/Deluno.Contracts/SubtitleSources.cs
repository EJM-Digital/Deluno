namespace Deluno.Contracts;

/// <summary>
/// Where a subtitle Deluno holds came from.
///
/// The premise of Subber inside Deluno is that Deluno knows what it has,
/// because it put most of it there. <see cref="Fetched"/> is that case, and it
/// is written at the moment the file lands rather than discovered later. The
/// other two are how Deluno learns about the subtitles it did not fetch —
/// which, on the day you turn languages on, is all of them.
/// </summary>
public static class SubtitleSources
{
    /// <summary>A track inside the video container, found by ffprobe.</summary>
    public const string Embedded = "embedded";

    /// <summary>
    /// A subtitle file beside the video that Deluno did not fetch — from the
    /// release, from a previous Bazarr, or dropped there by hand.
    /// </summary>
    public const string External = "external";

    /// <summary>
    /// A subtitle file Deluno fetched and wrote. Recorded with its provider, so
    /// a later rescan of the folder does not turn Deluno's own work into an
    /// anonymous file it knows nothing about.
    /// </summary>
    public const string Fetched = "fetched";

    public static readonly IReadOnlyList<string> All = [Embedded, External, Fetched];

    public static bool IsKnown(string? value)
        => value is not null && All.Contains(value.Trim().ToLowerInvariant());
}
