namespace Deluno.Contracts;

/// <summary>
/// What Deluno decided about one language for one file.
///
/// <para><b>Five names, and no number.</b> Bazarr answers this question with a
/// score out of a hundred and two thresholds — 96 for series, 86 for movies —
/// which a person has to be told the meaning of before they can read their own
/// library. #321 settled that Deluno would not import that homework: the states
/// underneath it are few and each has a name, so the names are the answer.</para>
///
/// <para>The states already existed and were spelled out in free text at four
/// different call sites, which is how "watchable but not provably in time" came
/// to be phrased three ways. This is that vocabulary, once.</para>
/// </summary>
public enum SubtitleDecision
{
    /// <summary>
    /// Nothing usable was found or offered: no provider had it, what came back
    /// was not a subtitle, or every candidate was refused by the library's own
    /// release-name rules.
    /// </summary>
    Rejected = 0,

    /// <summary>
    /// A provider answered and Deluno could not tell whether the answer is
    /// right — the evidence is missing or disagrees with itself. Distinct from
    /// <see cref="Rejected"/> because nobody said no; the question is open.
    /// </summary>
    NeedsReview = 1,

    /// <summary>
    /// A subtitle is on disk and watchable, and its timing has a proven defect
    /// the library's repair policy covers. The repair is queued, not owed to a
    /// person.
    /// </summary>
    NeedsSync = 2,

    /// <summary>
    /// On disk and watchable, but below the target: cut from another release, so
    /// the timing is not provably right and a better one may be uploaded
    /// tomorrow. Deluno keeps looking, on a backoff.
    /// </summary>
    UsableFallback = 3,

    /// <summary>
    /// Made for this file. The timing is guaranteed, there is nothing better to
    /// find, and the search for this language stops.
    /// </summary>
    MeetsSubtitlePlan = 4
}

/// <summary>How many languages landed in each state during one run.</summary>
public sealed record SubtitleDecisionTally(
    int Rejected = 0,
    int NeedsReview = 0,
    int NeedsSync = 0,
    int UsableFallback = 0,
    int MeetsSubtitlePlan = 0)
{
    public int Total => Rejected + NeedsReview + NeedsSync + UsableFallback + MeetsSubtitlePlan;

    /// <summary>How many are on disk and watchable, whatever else is owed.</summary>
    public int Held => NeedsSync + UsableFallback + MeetsSubtitlePlan;

    public SubtitleDecisionTally With(SubtitleDecision decision)
        => decision switch
        {
            SubtitleDecision.Rejected => this with { Rejected = Rejected + 1 },
            SubtitleDecision.NeedsReview => this with { NeedsReview = NeedsReview + 1 },
            SubtitleDecision.NeedsSync => this with { NeedsSync = NeedsSync + 1 },
            SubtitleDecision.UsableFallback => this with { UsableFallback = UsableFallback + 1 },
            _ => this with { MeetsSubtitlePlan = MeetsSubtitlePlan + 1 }
        };
}

public static class SubtitleDecisions
{
    /// <summary>
    /// The stored name. Kebab-case because it goes in a database column beside
    /// the other typed results, not on screen.
    /// </summary>
    public static string ToStoredName(this SubtitleDecision decision)
        => decision switch
        {
            SubtitleDecision.Rejected => "rejected",
            SubtitleDecision.NeedsReview => "needs-review",
            SubtitleDecision.NeedsSync => "needs-sync",
            SubtitleDecision.UsableFallback => "usable-fallback",
            _ => "meets-subtitle-plan"
        };

    /// <summary>
    /// What a person reading Activity is told about one run.
    ///
    /// <para>Only the states that actually happened are named. A run where
    /// everything landed is one clause, not five with four zeroes in them.</para>
    /// </summary>
    public static string Describe(SubtitleDecisionTally tally, string libraryName)
    {
        if (tally.Total == 0)
        {
            return $"Nothing in {libraryName} was short of a subtitle.";
        }

        List<string> parts = [];
        if (tally.MeetsSubtitlePlan > 0)
        {
            parts.Add($"{tally.MeetsSubtitlePlan} made for the exact file");
        }

        if (tally.NeedsSync > 0)
        {
            parts.Add($"{tally.NeedsSync} queued to be timed against the audio");
        }

        if (tally.UsableFallback > 0)
        {
            parts.Add($"{tally.UsableFallback} watchable but still worth upgrading");
        }

        if (tally.NeedsReview > 0)
        {
            parts.Add($"{tally.NeedsReview} that need a look");
        }

        if (tally.Rejected > 0)
        {
            parts.Add($"{tally.Rejected} nobody had");
        }

        return tally.Held == 0
            ? $"Looked for {tally.Total} subtitle(s) in {libraryName}: {Join(parts)}."
            : $"Fetched {tally.Held} of {tally.Total} subtitle(s) for {libraryName}: {Join(parts)}.";
    }

    private static string Join(IReadOnlyList<string> parts)
        => parts.Count == 1
            ? parts[0]
            : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
}
