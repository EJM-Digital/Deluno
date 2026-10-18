namespace Deluno.Contracts;

/// <summary>
/// What started a background job, in plain words -- the one place this is
/// written (#1086). A job's <c>source</c> is a code such as
/// <c>grab-rejected</c> or <c>queue-derived</c>; System, Activity, the job
/// drawer and the messages the job store writes all read the label from here so
/// the code never reaches a person. A test fails if any source an enqueue site
/// passes has no label here. A source nobody has labelled reads "Deluno".
/// </summary>
public static class JobSourceLabels
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        // You and Deluno itself
        ["manual"] = "You",
        // The Transfers seed route that only the smoke tests switch on (Testing:EnableTransfersSeedEndpoint).
        ["seeded"] = "Test setup",
        ["manual-season"] = "You",
        ["system"] = "Deluno",
        ["automation"] = "Deluno's automatic search",
        ["automatic-season"] = "The automatic library check",
        ["mislabelled-file"] = "Deluno, replacing a file that was another episode",
        ["rss"] = "The feed check",

        // Outside apps
        ["webhook"] = "An outside app",
        ["external"] = "An outside app",
        ["external-redownload"] = "An outside app asking for another copy",

        // Libraries and lists
        ["movies"] = "The film library",
        ["movie"] = "The film library",
        ["series"] = "The TV library",
        ["tv"] = "The TV library",
        ["intake"] = "A list sync",
        ["bulk"] = "A bulk action",
        ["metadata"] = "A details refresh",
        ["collections"] = "A collection refresh",
        ["title.removed"] = "A title you removed",

        // Importing and reading files
        ["filesystem"] = "An import you started",
        ["filesystem.import"] = "An import",
        ["filesystem.import.season-pack"] = "A season that was only partly imported",
        ["import-refused"] = "An import that was refused",
        ["library-import"] = "A library import",
        ["library-import-resume"] = "A library import picking up again",
        ["existing-library-import"] = "A scan of a folder you already had",
        ["library-media-probe"] = "Reading what your files are",
        ["processor"] = "A processor's finished file",
        ["processor-output-watch"] = "A processor's finished file",
        ["processor-rejected"] = "A file a processor refused",

        // Downloads and their clients
        ["download-client"] = "A download client",
        ["download_dispatch.retry"] = "A download you retried",
        ["download-client-connectivity-retry"] = "A download client that could not be reached",
        ["download-health"] = "The download health check",
        ["download-integrity-failed"] = "A damaged download",
        ["grab-rejected"] = "A refused release",
        ["queue-derived"] = "A download in the queue",
        ["recovery-case.retry"] = "A problem you asked to try again",
        ["media-server-refresh-retry"] = "A media server that had not answered"
    };

    /// <summary>Every source that has a label of its own.</summary>
    public static IReadOnlyCollection<string> KnownSources => Labels.Keys;

    public static bool IsKnown(string? source) =>
        !string.IsNullOrWhiteSpace(source) && Labels.ContainsKey(source.Trim());

    /// <summary>The plain-words name of what started a job. A source nobody has labelled reads "Deluno", never the code.</summary>
    public static string For(string? source) =>
        !string.IsNullOrWhiteSpace(source) && Labels.TryGetValue(source.Trim(), out var label) ? label : "Deluno";

    /// <summary>The label with a lower-case start, for the middle of a sentence ("started by you").</summary>
    public static string ForSentence(string? source)
    {
        var label = For(source);
        return label.Length > 1 && char.IsUpper(label[0]) && !char.IsUpper(label[1]) && !label.StartsWith("Deluno", StringComparison.Ordinal)
            ? char.ToLowerInvariant(label[0]) + label[1..]
            : label;
    }
}
