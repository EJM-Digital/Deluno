namespace Deluno.Retention;

/// <summary>What happens on the day a Leaving soon title's wait is over (#494, DESIGN-008).</summary>
public static class RetentionRemovalModes
{
    /// <summary>Removed on the day, if it still matches and every server it depends on answered. The default.</summary>
    public const string Automatic = "automatic";

    /// <summary>Moves to Ready to remove, and waits for a person to confirm.</summary>
    public const string Ask = "ask";

    public static string Normalize(string? value)
        => string.Equals(value?.Trim(), Ask, StringComparison.OrdinalIgnoreCase) ? Ask : Automatic;
}

/// <summary>
/// What a rule does with a title on its day (#1011). Maintainerr's collection
/// handler offers the same choices (delete, unmonitor, do nothing, tag); Deluno
/// is the *arr, so "tag" gives the title a Deluno tag.
/// </summary>
public static class RetentionExpiryActions
{
    /// <summary>Files to the recycle bin and the title removed. What every rule did before the choice existed.</summary>
    public const string Delete = "delete";

    /// <summary>Stop monitoring; the files stay, so nothing more is searched for or upgraded.</summary>
    public const string Unmonitor = "unmonitor";

    /// <summary>Nothing changes; the day is only written down in Activity.</summary>
    public const string Log = "log";

    /// <summary>The title is given the rule's tag; nothing else changes.</summary>
    public const string Tag = "tag";

    public static readonly string[] All = [Delete, Unmonitor, Log, Tag];

    public static bool IsKnown(string? value) => All.Contains(value?.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A value read from storage or a request. Absent is <see cref="Delete"/>, which is what a
    /// rule saved before the choice existed did; a value nobody knows is the gentlest
    /// choice, <see cref="Log"/>, never a deletion.
    /// </summary>
    public static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? Delete : All.FirstOrDefault(known => string.Equals(known, value.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Log;

    /// <summary>What will happen on the day, in the words a screen prints.</summary>
    public static string Describe(string action, string? tag)
        => Normalize(action) switch
        {
            Unmonitor => "It stops being monitored. Its files stay.",
            Log => "Nothing changes. Deluno only writes it down in Activity.",
            Tag => $"It is given the tag “{tag}”. Nothing else changes.",
            _ => "Its files go to the recycle bin and it is removed."
        };
}

public static class RetentionLimits
{
    public const int MaxTagLength = 60;
    public const int MinPostponeDays = 1;
    public const int MaxPostponeDays = 3650;

    /// <summary>Why a postpone of this many days is refused, or null when it is fine.</summary>
    public static string? PostponeProblem(int? days)
        => days is { } count && count >= MinPostponeDays && count <= MaxPostponeDays
            ? null
            : $"Postpone by a whole number of days from {MinPostponeDays} to {MaxPostponeDays}.";
    public const int DefaultWaitDays = 14;
    public const int MinWaitDays = 1;
    public const int MaxWaitDays = 365;
    public const int DefaultCheckIntervalHours = 8;
    public const int MinCheckIntervalHours = 1;
    public const int MaxCheckIntervalHours = 168;
}

/// <summary>
/// One rule: what it acts on, the steps that choose it, which servers it reads,
/// how long a match waits and what happens then (#494, DESIGN-008).
/// </summary>
/// <param name="Level"><c>movie</c>, <c>show</c>, <c>season</c> or <c>episode</c>.</param>
/// <param name="Steps">
/// Keep only / Also include steps and blocks, each condition in the
/// <c>field:operator:value</c> shape the library filters use plus the rule
/// fields (<see cref="RetentionFieldCatalog"/>). Data, not code: a new
/// combination is configuration.
/// </param>
/// <param name="MediaServerIds">Servers whose answers the rule reads, and which a removal tells.</param>
/// <param name="RemoveFromServers">Whether a removal also tells those servers.</param>
/// <param name="WaitDays">How long a title stays Leaving soon before its removal day.</param>
/// <param name="RemovalMode"><see cref="RetentionRemovalModes"/>.</param>
/// <param name="CheckIntervalHours">How often the rule is checked.</param>
/// <param name="ExpiryAction"><see cref="RetentionExpiryActions"/>: what happens on a title's day (#1011).</param>
/// <param name="ExpiryTag">The tag the <c>tag</c> action gives.</param>
/// <param name="KeepInDelunoOnly">The rule's Leaving soon list is never made a collection on a media server (#1009).</param>
/// <param name="DeferWhilePlaying">A removal waits, and is tried at the next check, while the title is being played (#1012).</param>
public sealed record RetentionRule(
    string Id,
    string Name,
    string Level,
    bool IsEnabled,
    IReadOnlyList<RetentionStep> Steps,
    IReadOnlyList<string> MediaServerIds,
    bool RemoveFromServers,
    int WaitDays,
    string RemovalMode,
    int CheckIntervalHours,
    DateTimeOffset? LastCheckedUtc,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    string ExpiryAction = RetentionExpiryActions.Delete,
    string? ExpiryTag = null,
    bool KeepInDelunoOnly = false,
    bool DeferWhilePlaying = false)
{
    public string MediaType => RetentionLevels.MediaType(Level);

    public DateTimeOffset? NextCheckUtc => IsEnabled ? (LastCheckedUtc ?? CreatedUtc).AddHours(CheckIntervalHours) : null;
}

/// <param name="Conditions">The first version's flat list; read as a run of Keep only steps when <paramref name="Steps"/> is absent.</param>
public sealed record SaveRetentionRuleRequest(
    string? Name,
    string? Level,
    bool IsEnabled,
    IReadOnlyList<RetentionStep>? Steps,
    IReadOnlyList<string>? MediaServerIds,
    bool RemoveFromServers,
    int? WaitDays = null,
    string? RemovalMode = null,
    int? CheckIntervalHours = null,
    string? MediaType = null,
    IReadOnlyList<string>? Conditions = null,
    string? ExpiryAction = null,
    string? ExpiryTag = null,
    bool? KeepInDelunoOnly = null,
    bool? DeferWhilePlaying = null);

public static class RetentionRunKinds
{
    /// <summary>A rule looked at the library: what it marked, cleared, removed.</summary>
    public const string Check = "check";

    /// <summary>A person confirmed Ready to remove titles.</summary>
    public const string Confirmed = "confirmed";

    // What the first version recorded; kept so its history still reads.
    public const string Preview = "preview";
    public const string Cleanup = "cleanup";
}

public static class RetentionItemOutcomes
{
    /// <summary>Would be selected. The builder's answer.</summary>
    public const string Matched = "matched";

    /// <summary>A step could not be answered for it, so it was neither kept nor added. Never removed.</summary>
    public const string Undecided = "undecided";

    /// <summary>Left out because a server the rule depends on could not be asked.</summary>
    public const string ExcludedUnreachable = "excluded-unreachable";

    /// <summary>Newly marked Leaving soon.</summary>
    public const string Marked = "marked";

    /// <summary>No longer Leaving soon: it stopped matching, or somebody kept it.</summary>
    public const string Cleared = "cleared";

    /// <summary>Its wait is over and the rule asks first: waiting for a person.</summary>
    public const string Ready = "ready";

    /// <summary>Its day came but it was not removed: a server could not be asked, or a step could not be answered.</summary>
    public const string Held = "held";

    public const string Removed = "removed";

    /// <summary>Its day came and the rule's action was not a removal: it was recorded, unmonitored or tagged (#1011).</summary>
    public const string Done = "done";

    /// <summary>The first version's "in the preview, no longer matched".</summary>
    public const string Changed = "changed";

    public const string Failed = "failed";
}

/// <summary>What a check or a confirmation did.</summary>
/// <param name="MatchedCount">Titles Leaving soon for this rule after the run.</param>
/// <param name="ExcludedCount">Titles held or undecided.</param>
public sealed record RetentionRun(
    string Id,
    string RuleId,
    string RuleName,
    string Kind,
    string Trigger,
    string? PreviewRunId,
    DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc,
    int MatchedCount,
    int ExcludedCount,
    int RemovedCount,
    int FailedCount,
    long Bytes,
    string Summary,
    IReadOnlyList<RetentionRunItem>? Items = null);

/// <param name="Reasons">Why it matched, one sentence per step, in the words the screen prints.</param>
public sealed record RetentionRunItem(
    string EntityId,
    string MediaType,
    string Title,
    int? Year,
    long? SizeBytes,
    IReadOnlyList<string> Reasons,
    string Outcome,
    string? Message);

/// <summary>A field a rule can use, for the step builder.</summary>
/// <param name="Source"><c>deluno</c>, <c>server</c> or <c>disk</c>.</param>
/// <param name="UnsupportedOn">Server kinds that cannot answer it.</param>
public sealed record RetentionField(
    string Id,
    string Label,
    string Hint,
    string Group,
    string ValueKind,
    IReadOnlyList<string> Operators,
    IReadOnlyList<string>? Options,
    string Source,
    IReadOnlyList<string> UnsupportedOn,
    bool PerPerson = false);

/// <summary>What a rule would select right now, for the builder: nothing is saved.</summary>
public sealed record RetentionTrial(
    int Considered,
    IReadOnlyList<RetentionStepCount> StepCounts,
    int MatchedCount,
    long MatchedBytes,
    IReadOnlyList<RetentionRunItem> Sample,
    int UndecidedCount,
    IReadOnlyList<string> UnreachableServers,
    IReadOnlyList<string> Problems,
    IReadOnlyList<RetentionUnmanagedItem> Unmanaged,
    string ExpiryAction = RetentionExpiryActions.Delete,
    string ExpiryDescription = "");

public static class RetentionMarkStates
{
    /// <summary>Waiting for its removal day.</summary>
    public const string Leaving = "leaving";

    /// <summary>Its day has come and the rule asks first.</summary>
    public const string Ready = "ready";

    /// <summary>Not removed on its day: a server could not be asked, or a step could not be answered. Tried again at the next check.</summary>
    public const string Held = "held";

    /// <summary>Its day came and the rule's action, which is not a removal, has been done. It stays until it stops matching or is kept (#1011).</summary>
    public const string Done = "done";
}

/// <summary>A title a rule has marked Leaving soon (#494).</summary>
/// <param name="EpisodeIds">A season's or episode's episodes; empty for a movie or whole show.</param>
/// <param name="StateReason">Why a held title was not removed.</param>
public sealed record RetentionMark(
    string RuleId,
    string RuleName,
    string EntityId,
    string Level,
    string? ShowId,
    int? SeasonNumber,
    IReadOnlyList<string> EpisodeIds,
    string Title,
    int? Year,
    long? SizeBytes,
    IReadOnlyList<string> Reasons,
    DateTimeOffset FirstMatchedUtc,
    DateTimeOffset RemoveAfterUtc,
    string State,
    string? StateReason,
    DateTimeOffset UpdatedUtc)
{
    public string MediaType => RetentionLevels.MediaType(Level);

    /// <summary>What the mark was matched to on each server at its last check (#521).</summary>
    public IReadOnlyList<RetentionServerItem> ServerItems { get; init; } = [];

    /// <summary>
    /// A person put this title on the list by hand (#1008). The rule's steps no
    /// longer decide whether it stays; only a keep, or the title leaving
    /// Deluno, takes it off.
    /// </summary>
    public bool Manual { get; init; }
}

/// <param name="Problem">Why nothing was added, in words for the screen; null when it worked.</param>
public sealed record RetentionManualAddResult(RetentionMark? Mark, string? Problem);

/// <param name="EntityId">The movie, show, season or episode to add to the rule's list.</param>
public sealed record AddRetentionTitleRequest(string? EntityId);

/// <param name="Days">How many days to push each removal back.</param>
public sealed record PostponeRetentionRequest(string? RuleId, IReadOnlyList<string>? EntityIds, int? Days);

/// <summary>A title a person can add to a rule's list by hand.</summary>
public sealed record RetentionAddable(string EntityId, string Level, string Title, int? Year, bool AlreadyListed, bool Kept);

/// <summary>A marked title's item on one server: what goes in that server's Leaving soon collection.</summary>
/// <param name="Level">The server item's level: movie, show, season or episode.</param>
/// <param name="SectionId">Plex's library section.</param>
public sealed record RetentionServerItem(string ServerId, string ItemId, string Level, string? SectionId);

/// <summary>How the last attempt to bring one server's Leaving soon collection into line went.</summary>
/// <param name="Skipped">Titles that server cannot hold in the collection, such as a season on Plex.</param>
/// <param name="Problem">Why the last attempt failed; null when it worked.</param>
public sealed record RetentionCollectionState(
    string ServerId,
    string MediaType,
    int Members,
    int Skipped,
    string? Problem,
    DateTimeOffset AttemptedUtc,
    DateTimeOffset? SyncedUtc);

/// <summary>A title somebody kept: from one rule, or from all of them (#494).</summary>
/// <param name="RuleId">Null when kept from every rule.</param>
public sealed record RetentionKeep(
    string Id,
    string EntityId,
    string Level,
    string Title,
    string? RuleId,
    string? RuleName,
    string? Note,
    DateTimeOffset CreatedUtc);

/// <param name="RuleId">The rule to keep it from; null for every rule.</param>
/// <param name="Title">Needed only when the title is not Leaving soon.</param>
public sealed record SaveRetentionKeepRequest(string? EntityId, string? Level, string? RuleId, string? Note, string? Title = null);

/// <summary>An episode a rule removed while its show stayed: the reason it is no longer wanted.</summary>
public sealed record RetentionEpisodeRemoval(
    string EpisodeId,
    string SeriesId,
    int? SeasonNumber,
    int? EpisodeNumber,
    string RuleId,
    string RuleName,
    string Reason,
    DateTimeOffset RemovedUtc);

/// <summary>Everything retention says about one movie or show, for its own page.</summary>
public sealed record RetentionTitleStatus(
    IReadOnlyList<RetentionMark> Marks,
    IReadOnlyList<RetentionKeep> Keeps,
    IReadOnlyList<RetentionEpisodeRemoval> EpisodeRemovals);

/// <param name="EntityIds">Ready to remove titles to remove now.</param>
public sealed record RetentionConfirmRequest(string? RuleId, IReadOnlyList<string>? EntityIds);

public sealed record RetentionKeepResult(RetentionKeep Keep, int ClearedMarks);
