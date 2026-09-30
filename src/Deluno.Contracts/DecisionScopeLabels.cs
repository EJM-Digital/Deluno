namespace Deluno.Contracts;

/// <summary>
/// What a recorded decision was about, in plain words -- the one place this is
/// written (#1086). A decision's <c>scope</c> is a code such as
/// <c>series.season-search</c> or <c>filesystem.import.transfer</c>; the
/// decision lists on the film and show pages read the label from here so the
/// code never reaches a person. A scope nobody has labelled reads "A decision".
/// </summary>
public static class DecisionScopeLabels
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["series.season-search"] = "Searching for a season",
        ["series.episode-search"] = "Searching for an episode",
        ["series.search"] = "Searching for a show",
        ["movie.search"] = "Searching for a film",
        ["movie.search.manual"] = "A search you started for a film",
        ["series.search.manual"] = "A search you started for a show",
        ["movie.grab.manual"] = "A release you chose for a film",
        ["movie.grab.force"] = "A release you chose for a film, overriding the rules",
        ["series.grab.manual"] = "A release you chose for a show",
        ["series.grab.force"] = "A release you chose for a show, overriding the rules",
        ["filesystem.import"] = "Importing a download",
        ["filesystem.import.season-pack"] = "Importing a season pack",
        ["filesystem.import.transfer"] = "Moving files into the library",
        ["job.failure"] = "A background task that failed",
        ["job.retry"] = "Trying background tasks again"
    };

    public static IReadOnlyCollection<string> KnownScopes => Labels.Keys;

    public static bool IsKnown(string? scope) => !string.IsNullOrWhiteSpace(scope) && Labels.ContainsKey(scope.Trim());

    public static string For(string? scope) =>
        !string.IsNullOrWhiteSpace(scope) && Labels.TryGetValue(scope.Trim(), out var label) ? label : "A decision";
}
