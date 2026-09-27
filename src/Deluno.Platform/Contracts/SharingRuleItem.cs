namespace Deluno.Platform.Contracts;

/// <summary>
/// One entry in the seeding-rule matrix (#820), matching Cleanuparr's Seeding
/// Rules: a category, a tracker-domain pattern, and/or a set of tags, with
/// its own sharing policy fields (each null meaning "use whatever this rule
/// falls back to") and the two guards that can hold a download at Wait even
/// once its ratio/time target is otherwise met.
///
/// A rule with none of Category/Tracker/Tags set never matches anything —
/// there is no such thing as a rule that matches every download, because that
/// is what the global policy already is.
/// </summary>
public sealed record SharingRuleItem(
    string Id,
    /// <summary>Evaluated lowest-number-first; the first matching enabled rule wins.</summary>
    int Priority,
    /// <summary>Matched against the download's category. Null/blank matches any category.</summary>
    string? CategoryPattern,
    /// <summary>
    /// Matched against the tracker's host, taken from the source indexer's own
    /// URL. <c>*.example.com</c> matches that domain and its subdomains; any
    /// other value is a plain substring match.
    /// </summary>
    string? TrackerPattern,
    /// <summary>Comma-separated tag names, matched against the source indexer's own tags.</summary>
    string? TagsCsv,
    string TagMatchMode,
    /// <summary>Null falls back to the layer beneath this rule (indexer override, then global).</summary>
    string? Mode,
    int? ForHours,
    double? UntilRatio,
    string? StuckAction,
    int? StuckAfterDays,
    string? CompletionAction,
    /// <summary>Holds the download at Wait rather than reclaiming it while fewer than this many peers are seeding it.</summary>
    int? MinSeeders,
    /// <summary>Holds the download at Wait rather than reclaiming it once it has gone this many days with no upload or download activity.</summary>
    int? MaxInactiveDays,
    bool IsEnabled,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc)
{
    public const string TagMatchAny = "any";
    public const string TagMatchAll = "all";

    public static string NormalizeTagMatchMode(string? value)
        => string.Equals(value?.Trim(), TagMatchAll, StringComparison.OrdinalIgnoreCase) ? TagMatchAll : TagMatchAny;
}
