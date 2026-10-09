namespace Deluno.Contracts;

/// <summary>
/// What started a search, in plain words, for the Activity lines a search writes (#1192).
/// A film added with "Search for it as soon as it is added" was reported as a library
/// check "on schedule", and the "Search now" request as a search by hand,
/// because the only two words the code had were "manual" and everything else. Every
/// start a search can have names itself here, in one place.
/// </summary>
public static class SearchStartedWords
{
    /// <summary>
    /// The end of "Queued a library check for Movies ...": when and why it was queued.
    /// An unknown or empty trigger is the schedule, which is what a library check with
    /// no other reason is.
    /// </summary>
    public static string WhenQueued(string? triggeredBy)
        => Normalize(triggeredBy) switch
        {
            "manual" or "manual-season" => "right away, because you asked",
            "movie-added" => "right away, because a film was just added",
            "series-added" => "right away, because a show was just added",
            "rss" => "because new releases appeared on your indexers",
            "recovery-retry" => "to try again after a problem",
            "grab-rejected" => "to find another release after one was refused",
            "health-replacement" => "to replace a download that went wrong",
            "download-integrity-failed" => "to replace a damaged download",
            "season-pack-partial-import" => "to fetch what a partly imported season was missing",
            "external-redownload" => "because an outside app asked for another copy",
            "mislabelled-file" => "to replace a file that was another episode",
            "automatic-season" => "as part of the automatic library check",
            _ => "on schedule"
        };


    private static string Normalize(string? triggeredBy)
        => triggeredBy?.Trim().ToLowerInvariant() ?? string.Empty;
}
