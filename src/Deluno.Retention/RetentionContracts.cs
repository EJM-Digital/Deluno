namespace Deluno.Retention;

/// <summary>When a rule runs without a person pressing a button (#494).</summary>
public static class RetentionScheduleModes
{
    /// <summary>Only when somebody asks. The default: nothing happens on its own.</summary>
    public const string Manual = "manual";

    /// <summary>Works out what would go, on a schedule, and removes nothing.</summary>
    public const string Preview = "preview";

    /// <summary>Removes what matches, on a schedule. Chosen explicitly, never the default.</summary>
    public const string Cleanup = "cleanup";

    public static string Normalize(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            Preview => Preview,
            Cleanup => Cleanup,
            _ => Manual
        };
}

/// <summary>
/// One rule: what it acts on, the steps that choose it, which servers it reads,
/// and when (#494, DESIGN-008).
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
public sealed record RetentionRule(
    string Id,
    string Name,
    string MediaType,
    bool IsEnabled,
    IReadOnlyList<string> MediaServerIds,
    bool RemoveFromServers,
    string ScheduleMode,
    int ScheduleIntervalHours,
    DateTimeOffset? LastScheduledRunUtc,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    string Level,
    IReadOnlyList<RetentionStep> Steps);

/// <param name="Conditions">The first version's flat list; read as a run of Keep only steps when <paramref name="Steps"/> is absent.</param>
public sealed record SaveRetentionRuleRequest(
    string? Name,
    string? MediaType,
    bool IsEnabled,
    IReadOnlyList<string>? Conditions,
    IReadOnlyList<string>? MediaServerIds,
    bool RemoveFromServers,
    string? ScheduleMode,
    int? ScheduleIntervalHours,
    string? Level = null,
    IReadOnlyList<RetentionStep>? Steps = null);

public static class RetentionRunKinds
{
    public const string Preview = "preview";
    public const string Cleanup = "cleanup";
}

public static class RetentionItemOutcomes
{
    /// <summary>Would be removed. A preview's answer.</summary>
    public const string Matched = "matched";

    /// <summary>A step could not be answered for it, so it was neither kept nor added. Never removed.</summary>
    public const string Undecided = "undecided";

    /// <summary>Left out because a server the rule depends on could not be asked.</summary>
    public const string ExcludedUnreachable = "excluded-unreachable";

    public const string Removed = "removed";

    /// <summary>In the preview, but no longer matched when the cleanup ran.</summary>
    public const string Changed = "changed";

    public const string Failed = "failed";
}

/// <summary>One run of a rule: a preview, or the cleanup that acted on one.</summary>
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

/// <param name="Reasons">Why it matched, one sentence per condition, in the words the screen prints.</param>
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
    IReadOnlyList<string> UnsupportedOn);

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
    IReadOnlyList<RetentionUnmanagedItem> Unmanaged);

public sealed record RetentionCleanupRequest(IReadOnlyList<string>? EntityIds);
