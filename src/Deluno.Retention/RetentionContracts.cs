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
/// One rule: which titles, judged by what, read from which servers, and when.
/// </summary>
/// <param name="Conditions">
/// Every condition must hold. The same <c>field:operator:value</c> strings the
/// library filters use, plus two that come from media servers - <c>watched</c>
/// and <c>lastPlayed</c>. Data, not code: a new combination is configuration.
/// </param>
/// <param name="MediaServerIds">Servers whose watch history the rule reads, and which a cleanup tells.</param>
/// <param name="RemoveFromServers">Whether a cleanup also removes the title from those servers.</param>
public sealed record RetentionRule(
    string Id,
    string Name,
    string MediaType,
    bool IsEnabled,
    IReadOnlyList<string> Conditions,
    IReadOnlyList<string> MediaServerIds,
    bool RemoveFromServers,
    string ScheduleMode,
    int ScheduleIntervalHours,
    DateTimeOffset? LastScheduledRunUtc,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

public sealed record SaveRetentionRuleRequest(
    string? Name,
    string? MediaType,
    bool IsEnabled,
    IReadOnlyList<string>? Conditions,
    IReadOnlyList<string>? MediaServerIds,
    bool RemoveFromServers,
    string? ScheduleMode,
    int? ScheduleIntervalHours);

public static class RetentionRunKinds
{
    public const string Preview = "preview";
    public const string Cleanup = "cleanup";
}

public static class RetentionItemOutcomes
{
    /// <summary>Would be removed. A preview's answer.</summary>
    public const string Matched = "matched";

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

/// <summary>A field a rule can use, for the condition builder.</summary>
public sealed record RetentionField(
    string Id,
    string Label,
    string Hint,
    string Group,
    string ValueKind,
    IReadOnlyList<string> Operators,
    IReadOnlyList<string>? Options,
    bool NeedsMediaServer);

public sealed record RetentionCleanupRequest(IReadOnlyList<string>? EntityIds);
