using System.Text.RegularExpressions;

namespace Deluno.Contracts;

/// <summary>
/// What each kind of background job says in plain words -- the one place this is
/// written (#1086). System, Activity, the queue, toasts and the messages the job
/// store writes all read the label from here, so a job type such as
/// <c>series.search.season</c> never reaches a person as a code. A test fails if
/// any job type Deluno can enqueue has no label here.
/// </summary>
public static class JobTypeLabels
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        // Searching
        ["episode.search"] = "Search for an episode",
        ["series.search.season"] = "Search for a season",
        ["series.search.manual"] = "Search for a show",
        ["movies.search.manual"] = "Search for a film",
        ["library.search.movies"] = "Search the film library",
        ["library.search.tv"] = "Search the TV library",
        ["library.search"] = "Search a library",

        // Checking the library and refreshing details
        ["movies.catalog.refresh"] = "Check the film library",
        ["series.catalog.refresh"] = "Check the TV library",
        ["movies.metadata.refresh"] = "Refresh film details",
        ["series.metadata.refresh"] = "Refresh show details",
        ["movies.quality.recalculate"] = "Recheck film quality",
        ["series.quality.recalculate"] = "Recheck show quality",
        ["movies.collection.sync"] = "Refresh a film collection",

        // Importing and reading files
        ["filesystem.import.execute"] = "Import a download",
        ["library.import.existing"] = "Scan a folder you already had",
        ["library.media.probe"] = "Read what your files are",

        // Subtitles
        ["library.subtitles.scan"] = "Check files for subtitles",
        ["library.subtitles.search"] = "Search for subtitles",
        ["subtitle.title.search"] = "Search for a title's subtitles",
        ["subtitle.sync"] = "Time a subtitle to the video",

        // Lists, media servers and download clients
        ["intake.sync"] = "Sync a list",
        ["media-servers.refresh"] = "Tell media servers about changed files",
        ["media-servers.refresh-retry"] = "Retry a media server that had not answered",
        ["download-client.retry-grab"] = "Resend a release to a download client"
    };

    private static readonly Regex CodePattern = BuildPattern();

    /// <summary>Every job type that has a label of its own.</summary>
    public static IReadOnlyCollection<string> KnownTypes => Labels.Keys;

    public static bool IsKnown(string? jobType) =>
        !string.IsNullOrWhiteSpace(jobType) && Labels.ContainsKey(jobType.Trim());

    /// <summary>The plain-words name of a job type. One nobody has labelled yet still reads as words, never the code.</summary>
    public static string For(string? jobType)
    {
        if (!string.IsNullOrWhiteSpace(jobType) && Labels.TryGetValue(jobType.Trim(), out var label))
        {
            return label;
        }

        return "Background task";
    }

    /// <summary>The label with a lower-case start, for the middle of a sentence ("Retrying search for an episode").</summary>
    public static string ForSentence(string? jobType) => LowerFirst(For(jobType));

    /// <summary>
    /// Rewrites any known job type in a message to its label: a leading
    /// <c>"type: "</c> becomes <c>"Label: "</c>, and one inside the text becomes
    /// the label in lower case. Messages written before this existed carry the
    /// code, so the activity reads rewrite them instead of migrating rows.
    /// </summary>
    public static string RewriteCodes(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message ?? string.Empty;
        }

        return CodePattern.Replace(message, match =>
        {
            var label = For(match.Value);
            return match.Index == 0 ? label : LowerFirst(label);
        });
    }

    private static string LowerFirst(string text) =>
        text.Length > 1 && char.IsUpper(text[0]) && !char.IsUpper(text[1]) ? char.ToLowerInvariant(text[0]) + text[1..] : text;

    private static Regex BuildPattern()
    {
        var alternatives = string.Join("|", Labels.Keys.OrderByDescending(key => key.Length).Select(Regex.Escape));
        return new Regex($@"(?<![\w.\-])(?:{alternatives})(?![\w\-]|\.\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
