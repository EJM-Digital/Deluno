namespace Deluno.Contracts;

/// <summary>
/// The rule kinds Deluno can actually detect today (#905, #951). Each is a
/// condition a download can be in, independent of any other kind, so a
/// download can trip more than one at once (different releases fail
/// differently).
///
/// <para>The wishlist in #905 named four more — magnet stuck fetching
/// metadata, never-starts-at-0%, tracker-says-unregistered as its own signal,
/// and strikeable dangerous-file/client-failure kinds. Tracker status is no
/// longer one of those: #878 gave every torrent client adapter a normalized
/// <see cref="Deluno.Contracts.DownloadTrackerStatuses"/> reading, and #818
/// folded "the tracker says unregistered" into <see cref="DeadTorrent"/>
/// alongside its existing zero-seeder check, rather than adding a fifth kind
/// for the same underlying dead-release verdict. #951 lands two more of the
/// wishlist: <see cref="ClientFailure"/>, once #879/#882-#885 gave the
/// evaluator a client-reported-failure signal to strike on, and
/// <see cref="MagnetMetadata"/>, once qBittorrent's adapter started telling
/// a magnet still fetching metadata apart from ordinary downloading. A
/// download starting at 0% and never moving is handled separately, as an
/// always-on informational finding rather than a rule kind — see
/// <see cref="Deluno.Recovery.Policies.DownloadHealthEvaluator"/>'s
/// zero-percent finding.</para>
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
    /// Zero peers or seeders at all, or the tracker itself explicitly
    /// reporting the torrent unregistered (#818) - a sharper, separate check
    /// from <see cref="LowPeers"/>'s configurable floor, with its own grace
    /// period before a freshly-added download can trip it. A tracker outage
    /// (temporarily unreachable) is not the same reading as "unregistered"
    /// and never trips this on its own.
    /// </summary>
    public const string DeadTorrent = "dead-torrent";

    /// <summary>
    /// A file inside the download matched the content blocklist (#816, #879)
    /// - the telemetry source #905's own doc comment said this kind was
    /// waiting on. Applies to both torrents and Usenet alike; the signal is
    /// the file listing, not the protocol.
    /// </summary>
    public const string DangerousFile = "dangerous-file";

    /// <summary>
    /// The download client itself reported this item stalled or errored
    /// (#951). This is a configurable twin of the always-on "client-stalled"
    /// finding the evaluator already produces for every item, whatever rules
    /// are configured — that unconditional finding still shows and still
    /// falls back to the platform-wide strike threshold on Search &amp;
    /// Cycles, so upgrading never changes what an existing install already
    /// does. Turning this rule on instead gives that same signal its own
    /// strikes, timer, scope and action, the same as every other rule here.
    /// Off by default (#936): a brand-new kind, so there is nothing for an
    /// existing install to have had on already.
    /// </summary>
    public const string ClientFailure = "client-failure";

    /// <summary>
    /// A torrent added as a magnet link that has not yet resolved its own
    /// metadata (name, file list, size) — qBittorrent's own <c>metaDL</c>
    /// state (#951). Only qBittorrent's adapter reports this signal today;
    /// Transmission and Deluge expose no equivalent field in what Deluno
    /// already reads from them, so this rule never trips for a download on
    /// those clients (see <see cref="Deluno.Contracts.RecoveryQueueSnapshot.IsFetchingMetadata"/>).
    /// Usenet has no magnets or metadata phase, so this never applies there
    /// either. Off by default (#936).
    /// </summary>
    public const string MagnetMetadata = "magnet-metadata";

    public static readonly IReadOnlyList<string> All = [NoProgress, TooSlow, LowPeers, DeadTorrent, DangerousFile, ClientFailure, MagnetMetadata];

    public static bool IsKnown(string kind) => All.Contains(kind, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether this kind ever needs a client the download reached over BitTorrent (as opposed to Usenet, which has no peers or seeders).</summary>
    public static bool RequiresTorrentSwarm(string kind)
        => string.Equals(kind, LowPeers, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(kind, DeadTorrent, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(kind, MagnetMetadata, StringComparison.OrdinalIgnoreCase);
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
/// Which tracker-privacy downloads a rule watches (#815). Cleanuparr's
/// "Privacy Type" per rule, plus its "Ignore Private"/"Delete Private"
/// toggles: a private-tracker account can be permanently banned for early
/// removal or a hit-and-run, so a person may want a gentler rule - or none
/// at all - for private trackers specifically.
/// </summary>
public static class DownloadHealthPrivacyScopes
{
    public const string Public = "public";
    public const string Private = "private";
    public const string Both = "both";

    public static readonly IReadOnlyList<string> All = [Public, Private, Both];

    public static bool IsKnown(string value) => All.Contains(value, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="isPrivate"/> null means the client never said - a rule
    /// scoped to public-only or private-only never matches an unknown
    /// privacy, only "both" does, so an unreadable flag can never wrongly
    /// narrow a private-tracker download's protection.
    /// </summary>
    public static bool Matches(string scope, bool? isPrivate)
        => string.Equals(scope, Both, StringComparison.OrdinalIgnoreCase) ||
           (isPrivate is { } known &&
            ((known && string.Equals(scope, Private, StringComparison.OrdinalIgnoreCase)) ||
             (!known && string.Equals(scope, Public, StringComparison.OrdinalIgnoreCase))));
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
    double Threshold = 0,
    /// <summary>#815: which tracker-privacy downloads this rule watches. "both" (the default) is every existing rule's value, so nothing narrows for an existing install until it is edited.</summary>
    string PrivacyScope = DownloadHealthPrivacyScopes.Both,
    /// <summary>
    /// <see cref="DownloadHealthRuleKinds.TooSlow"/> only (#951, G157): once
    /// a download has spent this many minutes in the queue, in total, it
    /// trips this rule regardless of its current speed - a hard ceiling
    /// under the ordinary speed-floor-and-timer check above, for a release
    /// that trickles just fast enough to keep dodging the floor. Null (the
    /// default) means no ceiling, matching every rule before this option
    /// existed.
    /// </summary>
    int? TooSlowMaxTotalMinutes = null,
    /// <summary>
    /// <see cref="DownloadHealthRuleKinds.TooSlow"/> only (#951, parity sheet
    /// G157): a download whose total size is above this many bytes never
    /// trips this rule - a large release is expected to take longer. Null or
    /// 0 (the default) means no size ceiling.
    /// </summary>
    long? IgnoreAboveSizeBytes = null,
    /// <summary>
    /// <see cref="DownloadHealthRuleKinds.TooSlow"/> only (#951): while the
    /// download client's own alternate/scheduled speed limit is turned on,
    /// this rule never trips - a download a person deliberately throttled is
    /// not unhealthy. False (the default) leaves every existing rule's
    /// behaviour unchanged. Only qBittorrent's and Transmission's adapters report whether this
    /// is on today; for every other client this option has no effect, since
    /// nothing about that client's speed limit is known.
    /// </summary>
    bool SkipWhileAlternateSpeedLimitOn = false,
    /// <summary>
    /// How many further bytes a download must gain, at minimum, for that
    /// gain to reset this rule's strikes (#951). Null or 0 (the default)
    /// keeps the original behaviour: any byte of further progress ends the
    /// spell, exactly as <see cref="ResetStrikesOnProgress"/> already
    /// documented before this option existed. Set only when
    /// <see cref="ResetStrikesOnProgress"/> is true; ignored otherwise.
    /// </summary>
    long? ResetStrikesMinimumProgressBytes = null,
    /// <summary>
    /// #1240: what this rule does to a torrent from a private tracker, or one grouped with a seeding tag, once its
    /// strikes reach the limit. <see cref="DownloadHealthPrivateActions.Remove"/> (the default, and the value of every
    /// rule saved before this existed) acts as the rule's action says; <see cref="DownloadHealthPrivateActions.MoveAndReplace"/>
    /// moves it to the keep-seeding category instead and looks for another copy. A public torrent is never moved.
    /// </summary>
    string PrivateAction = DownloadHealthPrivateActions.Remove,
    /// <summary>#1240: whether a move also blocks that release for the title, so the search does not pick it again. Read only with a move.</summary>
    bool BlocklistWhenMoved = true)
{
    public TimeSpan Timer => TimeSpan.FromMinutes(Math.Clamp(TimerMinutes, 1, 1440));

    public bool CoversCompletion(double progressPercent)
        => progressPercent >= MinCompletionPercent && progressPercent <= MaxCompletionPercent;

    public bool AppliesToProtocol(bool protocolIsUsenet) => DownloadHealthAppliesTo.Matches(AppliesTo, protocolIsUsenet);

    /// <summary>#815: whether this rule's privacy scope covers a download with this reported privacy.</summary>
    public bool AppliesToPrivacy(bool? isPrivate) => DownloadHealthPrivacyScopes.Matches(PrivacyScope, isPrivate);

    /// <summary>
    /// Whether this rule and <paramref name="other"/> could both match the
    /// same download at once: same kind, both enabled, overlapping protocol
    /// scope, overlapping completion range, overlapping privacy scope.
    /// Saving a rule that overlaps an existing one is refused (#905 decision
    /// 1) - two rules racing to strike the same download would leave
    /// whichever one is checked last winning by accident, with no way for a
    /// person reading the settings screen to know which that would be.
    /// </summary>
    public bool Overlaps(DownloadHealthRule other)
        => !string.Equals(Id, other.Id, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(Kind, other.Kind, StringComparison.OrdinalIgnoreCase) &&
           Enabled && other.Enabled &&
           ScopeOverlaps(AppliesTo, other.AppliesTo, DownloadHealthAppliesTo.Both) &&
           ScopeOverlaps(PrivacyScope, other.PrivacyScope, DownloadHealthPrivacyScopes.Both) &&
           MinCompletionPercent <= other.MaxCompletionPercent &&
           other.MinCompletionPercent <= MaxCompletionPercent;

    private static bool ScopeOverlaps(string a, string b, string both)
        => string.Equals(a, both, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(b, both, StringComparison.OrdinalIgnoreCase) ||
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

        if (!DownloadHealthPrivacyScopes.IsKnown(candidate.PrivacyScope))
        {
            return "Choose public, private, or both for this rule.";
        }

        if (candidate.MinCompletionPercent < 0 || candidate.MaxCompletionPercent > 100 ||
            candidate.MinCompletionPercent > candidate.MaxCompletionPercent)
        {
            return "The completion range must be between 0% and 100%, with the low end at or below the high end.";
        }

        if (candidate.StrikeLimit < 1 || candidate.StrikeLimit > 100)
        {
            return "Strikes must be between 1 and 100.";
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

        if (candidate.Kind == DownloadHealthRuleKinds.TooSlow)
        {
            if (candidate.TooSlowMaxTotalMinutes is { } maxTotalMinutes && (maxTotalMinutes < 1 || maxTotalMinutes > 43_200))
            {
                return "The maximum total time must be between 1 minute and 30 days.";
            }

            if (candidate.IgnoreAboveSizeBytes is { } ignoreAboveSizeBytes && ignoreAboveSizeBytes < 0)
            {
                return "The ignore-above size cannot be negative.";
            }
        }

        if (candidate.ResetStrikesMinimumProgressBytes is { } resetMinimumBytes && resetMinimumBytes < 0)
        {
            return "The minimum progress amount cannot be negative.";
        }

        if (!DownloadHealthPrivateActions.IsKnown(candidate.PrivateAction))
        {
            return "Choose whether a private tracker's torrent is removed or moved to keep seeding.";
        }

        if (DownloadHealthPrivateActions.Moves(candidate.PrivateAction))
        {
            if (!string.Equals(candidate.ActionPreset, DownloadHealthActionPresets.Replace, StringComparison.OrdinalIgnoreCase))
            {
                return "Moving a torrent to keep seeding replaces removing it, so this rule's action must be Replace.";
            }

            if (string.Equals(candidate.AppliesTo, DownloadHealthAppliesTo.Usenet, StringComparison.OrdinalIgnoreCase))
            {
                return "Only torrents can keep seeding, so this rule must cover torrents.";
            }

            if (string.Equals(candidate.Kind, DownloadHealthRuleKinds.DangerousFile, StringComparison.OrdinalIgnoreCase))
            {
                return "A dangerous or junk file is always removed and deleted, so this rule cannot keep it seeding.";
            }
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
        DownloadHealthRuleKinds.DangerousFile => "Dangerous or junk file",
        DownloadHealthRuleKinds.ClientFailure => "Client-reported failure",
        DownloadHealthRuleKinds.MagnetMetadata => "Magnet stuck fetching metadata",
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

/// <summary>
/// One entry on the ignore list (#817): a download matching any filled-in field is exempt from every rule.
/// <paramref name="Tag"/> (#1194) matches a tag the client put on the torrent, or a tag Deluno holds for the title
/// or indexer it came from. <paramref name="TrackerDomain"/> matches the host of the torrent's tracker announce
/// URL (a private tracker's domain, subdomains included) and, as it always has, the indexer's name.
/// </summary>
public sealed record DownloadHealthIgnoreEntry(
    string Id,
    string? Hash,
    string? Category,
    string? TrackerDomain,
    string? Note,
    DateTimeOffset CreatedUtc,
    string? Tag = null)
{
    /// <summary>Whether this entry names a tag, so the caller knows to look Deluno's own tags up.</summary>
    public bool NamesATag => !string.IsNullOrWhiteSpace(Tag);

    public bool Matches(
        string? hash,
        string? category,
        string? indexerName,
        IReadOnlyList<string>? clientTags = null,
        IReadOnlyList<string>? trackerHosts = null,
        IReadOnlyList<string>? delunoTags = null)
        => (Hash is { Length: > 0 } wantedHash && string.Equals(wantedHash, hash, StringComparison.OrdinalIgnoreCase)) ||
           (Category is { Length: > 0 } wantedCategory && string.Equals(wantedCategory, category, StringComparison.OrdinalIgnoreCase)) ||
           MatchesTracker(indexerName, trackerHosts) ||
           MatchesTag(clientTags, delunoTags);

    private bool MatchesTracker(string? indexerName, IReadOnlyList<string>? trackerHosts)
    {
        if (TrackerDomain is not { Length: > 0 } wanted) return false;
        var domain = DomainOf(wanted);
        if (domain.Length > 0 && trackerHosts is not null &&
            trackerHosts.Any(host => string.Equals(host, domain, StringComparison.OrdinalIgnoreCase) ||
                                     host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Today's match, which stays: the indexer's name containing what was typed.
        return !string.IsNullOrWhiteSpace(indexerName) && indexerName.Contains(wanted.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesTag(IReadOnlyList<string>? clientTags, IReadOnlyList<string>? delunoTags)
    {
        if (Tag is not { Length: > 0 } wanted) return false;
        var tag = wanted.Trim();
        return (clientTags ?? []).Concat(delunoTags ?? []).Any(candidate => string.Equals(candidate.Trim(), tag, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>What was typed as a tracker domain, reduced to a bare host: a pasted announce URL, port or path is dropped.</summary>
    internal static string DomainOf(string typed)
    {
        var text = typed.Trim().ToLowerInvariant();
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)) return uri.Host;
        var slash = text.IndexOf('/');
        if (slash >= 0) text = text[..slash];
        var colon = text.IndexOf(':');
        if (colon >= 0) text = text[..colon];
        return text.TrimStart('.');
    }
}
