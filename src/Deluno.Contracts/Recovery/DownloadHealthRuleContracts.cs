namespace Deluno.Contracts;

/// <summary>
/// The rule kinds Deluno can actually detect today (#905). Each is a condition
/// a download can be in, independent of any other kind, so a download can trip
/// more than one at once (different releases fail differently).
///
/// <para>The wishlist in #905 named four more — magnet stuck fetching
/// metadata, never-starts-at-0%, tracker-says-unregistered as its own signal,
/// and strikeable dangerous-file/client-failure kinds. None of those has a
/// telemetry source yet: no client adapter reports metadata-fetch state or a
/// normalized tracker-status enum, and the file/client-failure findings
/// (#879, #882-#885) are not built. Adding them here without a real signal
/// behind them would be a switch that does nothing, which is worse than not
/// offering it — so they are left for whichever of those issues lands the
/// telemetry first.</para>
/// </summary>
public static class DownloadHealthRuleKinds
{
    /// <summary>Nothing has arrived for the rule's own timer.</summary>
    public const string NoProgress = "no-progress";

    /// <summary>Slower than the rule's floor for the rule's own timer.</summary>
    public const string TooSlow = "too-slow";

    /// <summary>Fewer peers/seeders than the rule's floor for the rule's own timer.</summary>
    public const string LowPeers = "low-peers";

    /// <summary>
    /// Zero peers or seeders at all (#818) - a sharper, separate check from
    /// <see cref="LowPeers"/>'s configurable floor, with its own grace period
    /// before a freshly-added download can trip it.
    /// </summary>
    public const string DeadTorrent = "dead-torrent";

    public static readonly IReadOnlyList<string> All = [NoProgress, TooSlow, LowPeers, DeadTorrent];

    public static bool IsKnown(string kind) => All.Contains(kind, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether this kind ever needs a client the download reached over BitTorrent (as opposed to Usenet, which has no peers or seeders).</summary>
    public static bool RequiresTorrentSwarm(string kind)
        => string.Equals(kind, LowPeers, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(kind, DeadTorrent, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// What happens once a rule's strikes reach its limit (#905 decision 2).
/// </summary>
public static class DownloadHealthActionPresets
{
    /// <summary>Remove from the client, delete files, blocklist the release, search again.</summary>
    public const string Replace = "replace";

    /// <summary>Blocklist the release and search again, but leave it in the client untouched.</summary>
    public const string RetryElsewhere = "retry-elsewhere";

    /// <summary>Surface the finding; take no action on its own.</summary>
    public const string JustTellMe = "just-tell-me";

    public static readonly IReadOnlyList<string> All = [Replace, RetryElsewhere, JustTellMe];

    public static bool IsKnown(string preset) => All.Contains(preset, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Which protocol family a rule watches.</summary>
public static class DownloadHealthAppliesTo
{
    public const string Torrents = "torrents";
    public const string Usenet = "usenet";
    public const string Both = "both";

    public static readonly IReadOnlyList<string> All = [Torrents, Usenet, Both];

    public static bool IsKnown(string value) => All.Contains(value, StringComparer.OrdinalIgnoreCase);

    public static bool Matches(string appliesTo, bool protocolIsUsenet)
        => string.Equals(appliesTo, Both, StringComparison.OrdinalIgnoreCase) ||
           (protocolIsUsenet && string.Equals(appliesTo, Usenet, StringComparison.OrdinalIgnoreCase)) ||
           (!protocolIsUsenet && string.Equals(appliesTo, Torrents, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// One configured download-health rule (#905). Deluno can hold more than one
/// rule of the same <see cref="Kind"/> at once - for example a stricter "too
/// slow" floor for the first half of a download and a gentler one once it is
/// mostly there - as long as no two enabled rules of the same kind can ever
/// both match the same download (<see cref="Overlaps"/>).
/// </summary>
public sealed record DownloadHealthRule(
    string Id,
    string Kind,
    bool Enabled,
    /// <summary>How many strikes trip the action. 1-20; 1 is Cleanuparr's "instant".</summary>
    int StrikeLimit,
    /// <summary>How often, in minutes, a further strike can be added while the problem continues.</summary>
    int TimerMinutes,
    string AppliesTo,
    /// <summary>Percent complete (0-100) this rule covers, inclusive at both ends.</summary>
    int MinCompletionPercent,
    int MaxCompletionPercent,
    /// <summary>Any byte of further progress ends the spell early, the same as the problem clearing.</summary>
    bool ResetStrikesOnProgress,
    string ActionPreset,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    /// <summary>
    /// The one number each kind needs beyond the shared fields above, read
    /// differently per <see cref="Kind"/> so the schema stays one shape
    /// rather than a field per kind that is unused everywhere else:
    /// <see cref="DownloadHealthRuleKinds.TooSlow"/> reads it as a KB/s floor,
    /// <see cref="DownloadHealthRuleKinds.LowPeers"/> as a peer-count floor,
    /// <see cref="DownloadHealthRuleKinds.DeadTorrent"/> as a grace period in
    /// minutes since the download was added. Unused by
    /// <see cref="DownloadHealthRuleKinds.NoProgress"/>.
    /// </summary>
    double Threshold = 0)
{
    public TimeSpan Timer => TimeSpan.FromMinutes(Math.Clamp(TimerMinutes, 1, 1440));

    public bool CoversCompletion(double progressPercent)
        => progressPercent >= MinCompletionPercent && progressPercent <= MaxCompletionPercent;

    public bool AppliesToProtocol(bool protocolIsUsenet) => DownloadHealthAppliesTo.Matches(AppliesTo, protocolIsUsenet);

    /// <summary>
    /// Whether this rule and <paramref name="other"/> could both match the
    /// same download at once: same kind, both enabled, overlapping protocol
    /// scope, overlapping completion range. Saving a rule that overlaps an
    /// existing one is refused (#905 decision 1) - two rules racing to strike
    /// the same download would leave whichever one is checked last winning by
    /// accident, with no way for a person reading the settings screen to know
    /// which that would be.
    /// </summary>
    public bool Overlaps(DownloadHealthRule other)
        => !string.Equals(Id, other.Id, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(Kind, other.Kind, StringComparison.OrdinalIgnoreCase) &&
           Enabled && other.Enabled &&
           ScopeOverlaps(AppliesTo, other.AppliesTo) &&
           MinCompletionPercent <= other.MaxCompletionPercent &&
           other.MinCompletionPercent <= MaxCompletionPercent;

    private static bool ScopeOverlaps(string a, string b)
        => string.Equals(a, DownloadHealthAppliesTo.Both, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(b, DownloadHealthAppliesTo.Both, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Validates one rule against the others it would sit alongside (#905 decision 1).</summary>
public static class DownloadHealthRuleValidator
{
    /// <summary>Null when <paramref name="candidate"/> is valid; otherwise a plain-English refusal.</summary>
    public static string? Validate(DownloadHealthRule candidate, IReadOnlyList<DownloadHealthRule> existing)
    {
        if (!DownloadHealthRuleKinds.IsKnown(candidate.Kind))
        {
            return "That isn't a rule kind Deluno knows about.";
        }

        if (!DownloadHealthActionPresets.IsKnown(candidate.ActionPreset))
        {
            return "Choose one of the offered actions for this rule.";
        }

        if (!DownloadHealthAppliesTo.IsKnown(candidate.AppliesTo))
        {
            return "Choose torrents, Usenet, or both for this rule.";
        }

        if (candidate.MinCompletionPercent < 0 || candidate.MaxCompletionPercent > 100 ||
            candidate.MinCompletionPercent > candidate.MaxCompletionPercent)
        {
            return "The completion range must be between 0% and 100%, with the low end at or below the high end.";
        }

        if (candidate.StrikeLimit < 1 || candidate.StrikeLimit > 20)
        {
            return "Strikes must be between 1 and 20.";
        }

        if (candidate.TimerMinutes < 1 || candidate.TimerMinutes > 1440)
        {
            return "The timer must be between 1 minute and 24 hours.";
        }

        switch (candidate.Kind)
        {
            case DownloadHealthRuleKinds.TooSlow when candidate.Threshold <= 0:
                return "Set a speed floor above zero.";
            case DownloadHealthRuleKinds.LowPeers when candidate.Threshold < 1:
                return "Set a peer floor of at least 1.";
            case DownloadHealthRuleKinds.DeadTorrent when candidate.Threshold < 0 || candidate.Threshold > 1440:
                return "The grace period must be between 0 minutes and 24 hours.";
        }

        var conflict = existing.FirstOrDefault(candidate.Overlaps);
        if (conflict is not null)
        {
            return $"This overlaps the existing \"{DescribeKind(conflict.Kind)}\" rule covering {conflict.MinCompletionPercent}-{conflict.MaxCompletionPercent}% on {Describe(conflict.AppliesTo)}. Narrow the range or scope, or turn the other rule off first.";
        }

        return null;
    }

    public static string DescribeKind(string kind) => kind switch
    {
        DownloadHealthRuleKinds.NoProgress => "No progress",
        DownloadHealthRuleKinds.TooSlow => "Too slow",
        DownloadHealthRuleKinds.LowPeers => "Not enough peers",
        DownloadHealthRuleKinds.DeadTorrent => "Dead torrent",
        _ => kind
    };

    private static string Describe(string appliesTo) => appliesTo switch
    {
        DownloadHealthAppliesTo.Torrents => "torrents",
        DownloadHealthAppliesTo.Usenet => "Usenet",
        _ => "torrents and Usenet"
    };
}

/// <summary>
/// One entry exempting a download from every health rule (#817): matched by
/// download hash/queue item id, download-client category, or tracker/indexer
/// domain. A title-scoped exclusion from automated search is a different
/// concern (Huntarr's IgnoreTag) and stays out of this list.
/// </summary>
/// <summary>
/// The rule set an installation gets before it ever opens the settings
/// screen, and what a caller with no configured rules at all should treat as
/// the baseline (tests, a fixture that never loaded settings). Matches what
/// #659 originally hard-coded: only "no progress" is on.
/// </summary>
public static class DownloadHealthRules
{
    public static readonly IReadOnlyList<DownloadHealthRule> Defaults =
    [
        new DownloadHealthRule(
            Id: "default-no-progress",
            Kind: DownloadHealthRuleKinds.NoProgress,
            Enabled: true,
            StrikeLimit: 3,
            TimerMinutes: 30,
            AppliesTo: DownloadHealthAppliesTo.Both,
            MinCompletionPercent: 0,
            MaxCompletionPercent: 100,
            ResetStrikesOnProgress: true,
            ActionPreset: DownloadHealthActionPresets.JustTellMe,
            CreatedUtc: DateTimeOffset.MinValue,
            UpdatedUtc: DateTimeOffset.MinValue)
    ];
}

public sealed record DownloadHealthIgnoreEntry(
    string Id,
    string? Hash,
    string? Category,
    string? TrackerDomain,
    string? Note,
    DateTimeOffset CreatedUtc)
{
    public bool Matches(string? hash, string? category, string? trackerDomain)
        => (Hash is { Length: > 0 } wantedHash && string.Equals(wantedHash, hash, StringComparison.OrdinalIgnoreCase)) ||
           (Category is { Length: > 0 } wantedCategory && string.Equals(wantedCategory, category, StringComparison.OrdinalIgnoreCase)) ||
           (TrackerDomain is { Length: > 0 } wantedDomain && !string.IsNullOrWhiteSpace(trackerDomain) &&
            trackerDomain.Contains(wantedDomain, StringComparison.OrdinalIgnoreCase));
}
