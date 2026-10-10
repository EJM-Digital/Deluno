namespace Deluno.Contracts;

/// <summary>The three answers a health check can give (#909).</summary>
public static class HealthCheckStatuses
{
    /// <summary>The thing being checked is fine.</summary>
    public const string Ok = "ok";

    /// <summary>Something needs a person's attention.</summary>
    public const string Problem = "problem";

    /// <summary>Deluno could not tell, and says so rather than guessing either way.</summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// A setting that starts off under the "nothing on by default" rule (#936)
    /// and is worth switching on. Not a problem (#1060): it never counts as
    /// "needs a look", never turns Health from "All clear", and never writes a
    /// health.problem Activity line or sends a notification.
    /// </summary>
    public const string Suggestion = "suggestion";
}

/// <summary>
/// Every category a check can belong to, so System &#8594; Health, the
/// Dashboard tile, the bell and notifications can all group and filter the
/// same way (#909).
/// </summary>
public static class HealthCheckCategories
{
    public const string DownloadClient = "download-client";
    public const string Indexer = "indexer";
    public const string Library = "library";
    public const string Processor = "processor";
    public const string MediaServer = "media-server";
    public const string Metadata = "metadata";
    public const string SubtitleProvider = "subtitle-provider";
    public const string Seerr = "seerr";
    public const string System = "system";
    public const string Grab = "grab";

    /// <summary>The computer Deluno runs on: sleep settings, drive health, a pending restart, processor heat (#1098).</summary>
    public const string Machine = "machine";

    /// <summary>Every category a check runs for, so "check now" can ask for all of them (#1217).</summary>
    public static readonly string[] AllChecked =
    [
        DownloadClient, Indexer, Library, Processor, MediaServer, Metadata, SubtitleProvider, Seerr,
        System, Grab, Machine, Tags, Proxy, RemotePathMapping
    ];

    /// <summary>A one-time note (an upgrade changed something), never re-run by a check and cleared by hand.</summary>
    public const string Notice = "notice";

    /// <summary>Tags that leave a connection with no titles, or titles with no indexer or client (#1043).</summary>
    public const string Tags = "tags";

    /// <summary>An outbound or indexer proxy, when one is configured (#868).</summary>
    public const string Proxy = "proxy";

    /// <summary>A remote path mapping's local side, and a client's reported save path translated through it (#868).</summary>
    public const string RemotePathMapping = "remote-path";

    /// <summary>Appended to the outbound category of an advisory-tier health event (#1016).</summary>
    public const string OutboundWarningSuffix = ".advisory";

    /// <summary>
    /// The advisory tier (#1016): a problem here does not stop Deluno grabbing
    /// or importing, so it is a "warning" rather than an error. A connection
    /// hears about these only when its Include health warnings switch is on,
    /// as Radarr and Sonarr's IncludeHealthWarnings does.
    /// </summary>
    public static bool IsWarningTier(string category)
        => string.Equals(category, Metadata, StringComparison.OrdinalIgnoreCase)
            || string.Equals(category, SubtitleProvider, StringComparison.OrdinalIgnoreCase)
            || string.Equals(category, Seerr, StringComparison.OrdinalIgnoreCase)
            || string.Equals(category, Tags, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One health check's latest result, read by every surface that shows
/// problems (#909). <paramref name="Id"/> is stable across runs -- the same
/// check overwrites its own row rather than appending a new one, which is
/// what lets <see cref="ProblemSinceUtc"/> mean "since it first went wrong"
/// instead of "since Deluno last happened to look".
/// </summary>
public sealed record HealthCheckResultItem(
    string Id,
    string Category,
    string Name,
    string Status,
    string Message,
    string? Cause,
    string? Fix,
    string? DetailsJson,
    DateTimeOffset LastRunUtc,
    DateTimeOffset? ProblemSinceUtc,
    DateTimeOffset? SnoozedUntilUtc)
{
    /// <summary>
    /// A problem still counts on the Dashboard tile and lights the bell while
    /// it is snoozed -- snoozing hides it from demanding attention on Health,
    /// it does not make it not a problem (#909).
    /// </summary>
    public bool IsSnoozed(DateTimeOffset nowUtc) => SnoozedUntilUtc is { } until && until > nowUtc;
}

/// <summary>
/// One check's outcome before it is persisted -- everything a check reports
/// except the bookkeeping (<see cref="HealthCheckResultItem.ProblemSinceUtc"/>,
/// snooze state) that only the repository can know across runs.
/// </summary>
public sealed record HealthCheckOutcome(
    string Id,
    string Category,
    string Name,
    string Status,
    string Message,
    string? Cause = null,
    string? Fix = null,
    string? DetailsJson = null)
{
    public static HealthCheckOutcome Ok(string id, string category, string name, string message, string? detailsJson = null)
        => new(id, category, name, HealthCheckStatuses.Ok, message, DetailsJson: detailsJson);

    public static HealthCheckOutcome Problem(string id, string category, string name, string message, string? cause, string? fix, string? detailsJson = null)
        => new(id, category, name, HealthCheckStatuses.Problem, message, cause, fix, detailsJson);

    public static HealthCheckOutcome Suggestion(string id, string category, string name, string message, string? cause, string? fix, string? detailsJson = null)
        => new(id, category, name, HealthCheckStatuses.Suggestion, message, cause, fix, detailsJson);

    public static HealthCheckOutcome Unknown(string id, string category, string name, string message, string? detailsJson = null)
        => new(id, category, name, HealthCheckStatuses.Unknown, message, DetailsJson: detailsJson);
}

/// <summary>The counts a Dashboard tile and the bell need without reading every row (#909).</summary>
public sealed record HealthCheckSummary(
    int ProblemCount,
    int UnsnoozedProblemCount,
    int UnknownCount,
    int SuggestionCount,
    DateTimeOffset? LastRunUtc);

public sealed record HealthCheckListResponse(
    IReadOnlyList<HealthCheckResultItem> Checks,
    HealthCheckSummary Summary);
