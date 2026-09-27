namespace Deluno.Contracts;

/// <summary>
/// How urgently one activity category reads, from its own name -- the one
/// place this is decided. <c>SqliteJobStore</c> (Deluno.Jobs) and
/// <c>MonitoringService</c> (Deluno.Api) each kept their own copy,
/// and only one of them was ever updated when a category needed to move
/// (#914, #929: a held processor hand-off read as ordinary news because
/// only one copy knew to call it a warning).
/// </summary>
public static class ActivityEventSeverity
{
    /// <summary>
    /// Categories that are not routine news by themselves -- nothing in their
    /// own name says "failed", "warning" or "attention" -- but do need a
    /// person, so they must read as at least a warning everywhere severity is
    /// asked about: the bell, notifications, and Monitoring's own filter.
    /// </summary>
    private static readonly IReadOnlySet<string> ExplicitWarnings = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // A processor hand-off Deluno stopped waiting on: held for a person to
        // decide, not a routine event (#914, #929).
        "processing.stalled",
        "processing.timed-out",
        "processing.lost"
    };

    public static string For(string category)
    {
        if (ExplicitWarnings.Contains(category))
        {
            return "warning";
        }

        if (category.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("dead-letter", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            return "error";
        }

        if (category.Contains("warning", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("attention", StringComparison.OrdinalIgnoreCase))
        {
            return "warning";
        }

        if (category.Contains("completed", StringComparison.OrdinalIgnoreCase))
        {
            return "success";
        }

        return "info";
    }
}
