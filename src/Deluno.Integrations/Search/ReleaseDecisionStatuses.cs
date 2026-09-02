namespace Deluno.Integrations.Search;

/// <summary>
/// The decision-status vocabulary a search candidate can carry, and the one
/// place that says how far down a list each status belongs.
///
/// The ordering table used to be written inline in the planner as a literal
/// list of status strings, which is why "worse than your file" and "failed a
/// hard gate" had to share the single word "rejected" to be sorted correctly.
/// They are separate outcomes in the normative contract (#354 section 1), so
/// the reason a candidate cannot win is recorded here once, and the display
/// wording is derived from the status rather than from its position.
/// </summary>
public static class ReleaseDecisionStatuses
{
    /// <summary>Passed every gate and wins this search.</summary>
    public const string Preferred = "preferred";

    /// <summary>Passed every gate; usable, but not the winner.</summary>
    public const string Acceptable = "acceptable";

    /// <summary>Below cutoff but still usable.</summary>
    public const string Eligible = "eligible";

    /// <summary>Same proven satisfaction as the installed file.</summary>
    public const string Equivalent = "equivalent";

    /// <summary>
    /// Passed every hard gate, but the installed file wins the first
    /// differing preference family. Nothing rejected this release.
    /// </summary>
    public const string CurrentBetter = "current-better";

    /// <summary>Needs owner review before any automatic action.</summary>
    public const string Held = "held";

    /// <summary>Failed a hard safety, compatibility or acquisition gate.</summary>
    public const string Rejected = "rejected";

    /// <summary>
    /// How far down a typed search result a candidate belongs. Lower sorts
    /// first. Candidates that cannot become the automatic winner sort after
    /// those that can, so the first row is always a real choice; the typed
    /// comparator owns every order within a stage.
    /// </summary>
    public static int TypedStageRank(string? status)
        => status switch
        {
            Rejected => 3,
            Held or "delayed" or "risky" => 2,
            // Nothing is wrong with these releases; they just cannot improve
            // what is already installed, so they belong below usable ones.
            Equivalent or CurrentBetter => 1,
            _ => 0
        };

    /// <summary>
    /// Plain-English wording for a status, for sentences shown to the owner.
    /// The raw status token is an API value, not copy: telling somebody their
    /// best candidate is "current-better" explains nothing.
    /// </summary>
    public static string Describe(string? status)
        => status switch
        {
            Preferred => "the preferred candidate",
            Acceptable => "usable but not the best available",
            Eligible => "below your cutoff",
            Equivalent => "equivalent to the file you already have",
            CurrentBetter => "not as good as the file you already have",
            Held => "waiting for your review",
            "delayed" => "waiting for its timing rule to clear",
            "risky" => "usable only with caution",
            Rejected => "rejected by a hard rule",
            _ => status ?? "unclassified"
        };

    /// <summary>
    /// How far down a legacy (score-based) search result a candidate belongs.
    /// </summary>
    public static int LegacyStageRank(string? status)
        => status switch
        {
            Preferred => 0,
            Acceptable or Eligible => 1,
            Equivalent or CurrentBetter => 2,
            Held or "delayed" or "risky" => 3,
            Rejected => 4,
            _ => 3
        };
}
