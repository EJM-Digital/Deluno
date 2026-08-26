namespace Deluno.Connections.Contracts;

/// <summary>
/// What a search source's own operator calls it: a private tracker, a
/// semi-private one, or an open index.
///
/// Deluno branches on none of it. The thing that actually changes behaviour is
/// the sharing rule (#288) — how long a site expects you to keep sharing, and
/// what happens when that cannot be met — which is asked as a question in plain
/// words rather than inferred from a category.
///
/// So this is provenance, not configuration. It is worth keeping because a
/// migration from Prowlarr or an arr app carries it, and "this one was marked
/// private" is exactly the fact that should pre-answer the sharing question for
/// an imported source. Nothing else sets it, and nothing displays it: a value a
/// user cannot change and cannot act on has no business on a list.
///
/// It used to be normalised in two places with opposite defaults — the draft
/// endpoint said "private" and the repository said "public" — and neither was
/// wrong enough for anyone to notice, because nothing read the result. One
/// place now, and an honest default.
/// </summary>
public static class IndexerPrivacy
{
    public const string Private = "private";
    public const string SemiPrivate = "semi-private";
    public const string Public = "public";

    /// <summary>
    /// What Deluno stores when nobody has said. Better than guessing "public",
    /// which is a claim about someone's tracker that Deluno cannot support.
    /// </summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// Accepts what an arr export actually writes, including Prowlarr's
    /// camel-cased <c>semiPrivate</c>.
    /// </summary>
    public static string Normalize(string? value)
        => value?.Trim().ToLowerInvariant().Replace("-", string.Empty) switch
        {
            "private" => Private,
            "semiprivate" => SemiPrivate,
            "public" => Public,
            _ => Unknown
        };

    /// <summary>
    /// True where the site's operator expects something back. Both private and
    /// semi-private trackers police sharing; an open index does not, and an
    /// unknown one is not something to make assumptions about.
    /// </summary>
    public static bool ExpectsSharing(string? value)
        => Normalize(value) is Private or SemiPrivate;
}
