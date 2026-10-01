namespace Deluno.Contracts;

/// <summary>The plain groups a turned-down release falls into on the Live screen (#1098). The names are the JSON values.</summary>
public static class TurnedDownGroups
{
    public const string Language = "language";
    public const string NotBetter = "notBetter";
    public const string TooBig = "tooBig";
    public const string FewSeeders = "fewSeeders";
    public const string Fake = "fake";
    public const string NotQuality = "notQuality";
    public const string TooSmall = "tooSmall";
    public const string Rules = "rules";
    public const string TooOld = "tooOld";
    public const string Blocklisted = "blocklisted";
    public const string Other = "other";

    /// <summary>Every group, in the order the Live screen lists them.</summary>
    public static readonly IReadOnlyList<string> All = [Language, NotBetter, NotQuality, TooBig, TooSmall, FewSeeders, Fake, Rules, TooOld, Blocklisted, Other];

    public static string Label(string? group) => group switch
    {
        Language => "Not in a language you want",
        NotBetter => "No better than what you have",
        TooBig => "Bigger than your size limit",
        FewSeeders => "Too few people sharing it",
        Fake => "Fake or mislabelled",
        NotQuality => "Not a quality you want",
        TooSmall => "Too small for its quality",
        Rules => "Blocked by your release rules",
        TooOld => "Older than your indexer keeps",
        Blocklisted => "On your blocklist",
        _ => "Other reasons"
    };
}

/// <summary>
/// The ONE place Deluno's raw decision reasons (the sentences the decision engine,
/// the title match and the preference evaluator write) become the plain groups
/// Live shows (#1098). A reason nobody has taught this helper is "other", never lost.
/// </summary>
public static class TurnedDownReasons
{
    /// <summary>The decision statuses that mean "looked at and not taken" (a held or risky release is still a candidate).</summary>
    public static bool IsTurnedDown(string? status)
        => string.Equals(status, "rejected", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "current-better", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "equivalent", StringComparison.OrdinalIgnoreCase);

    /// <summary>The group for a release a person had blocked (it was skipped before it was scored).</summary>
    public const string BlocklistedReason = "On your blocklist.";

    /// <summary>
    /// The group for one raw reason. <paramref name="status"/> is the decision status when known: a release whose
    /// only fault is that your file is as good or better has no rejecting sentence of its own.
    /// </summary>
    public static string GroupFor(string? reason, string? status = null)
    {
        if (string.Equals(reason?.Trim(), BlocklistedReason, StringComparison.Ordinal))
        {
            return TurnedDownGroups.Blocklisted;
        }

        if (string.Equals(status, "current-better", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "equivalent", StringComparison.OrdinalIgnoreCase))
        {
            return TurnedDownGroups.NotBetter;
        }

        return Match(reason) ?? TurnedDownGroups.Other;
    }

    /// <summary>
    /// The first rejecting reason to record for a candidate: the gate that refused it when the engine named one,
    /// else the first of its reasons or risks that belongs to a known group. Null when nothing names a cause.
    /// </summary>
    public static string? FirstRejectingReason(string? gate, IEnumerable<string>? reasons, IEnumerable<string>? risks)
    {
        if (!string.IsNullOrWhiteSpace(gate))
        {
            return gate;
        }

        foreach (var list in new[] { risks, reasons })
        {
            var hit = list?.FirstOrDefault(reason => Match(reason) is not null);
            if (hit is not null)
            {
                return hit;
            }
        }

        return null;
    }

    private static string? Match(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return null;
        }

        var text = reason.Trim();

        bool Has(string part) => text.Contains(part, StringComparison.OrdinalIgnoreCase);

        // The most specific first: a name that is not the title asked for, or a file that is not what it says.
        if (Has("looks like a sample") || Has("blocked token") || Has("never-grab pattern")
            || Has("This release is for something else") || Has("the search was for") || Has("This release is from ") || Has("mislabelled") || Has("fake"))
        {
            return TurnedDownGroups.Fake;
        }

        if (Has("audio.language") || Has("language"))
        {
            return TurnedDownGroups.Language;
        }

        if (Has("No one is seeding") || Has("No seeders") || Has("seeder(s), below") || Has("Very low seed count") || Has("seeders, below"))
        {
            return TurnedDownGroups.FewSeeders;
        }

        if (Has("minimum configured for") || (Has("below the") && Has("minimum")))
        {
            return TurnedDownGroups.TooSmall;
        }

        if (Has("size exceeds") || (Has("is above the") && Has("maximum configured")))
        {
            return TurnedDownGroups.TooBig;
        }

        if (Has("Downgrade blocked") || Has("Equivalent replacement blocked") || Has("Upgrade stop policy")
            || Has("Stopped upgrading") || Has("than the copy you have") || Has("installed file is better")
            || Has("ranks lower") || Has("No preference in this plan ranks") || Has("equivalent proven satisfaction")
            || Has("below the current file") || Has("does not improve") || Has("does not persistently improve")
            || Has("already meets") || Has("lower than the current file"))
        {
            return TurnedDownGroups.NotBetter;
        }

        if (Has("not one of the qualities this profile allows") || Has("is below cutoff") || Has("is below its target"))
        {
            return TurnedDownGroups.NotQuality;
        }

        if (Has("retention window") || Has("retention limit"))
        {
            return TurnedDownGroups.TooOld;
        }

        if (Has("missing required term") || Has("contains excluded term") || Has("required word") || Has("ignored word")
            || Has("required trait") || Has("forbidden trait") || Has("required-any group") || Has("hard safety gate")
            || Has("searches only") || Has("TRaSH rates this copy"))
        {
            return TurnedDownGroups.Rules;
        }

        return null;
    }
}
