namespace Deluno.Quality.Guides;

/// <summary>
/// The owner-controlled state for periodic upstream guide checks. Enabling a
/// check permits a weekly, metadata-only comparison; it never syncs or applies
/// an upstream guide package.
/// </summary>
public sealed record GuideUpdateCheckState(
    bool IsEnabled,
    DateTimeOffset? LastCheckedUtc,
    string? LastSeenRevision,
    string Status,
    string? Error,
    GuideUpdateCheckReport? Report,
    DateTimeOffset UpdatedUtc);

public sealed record UpdateGuideUpdateCheckSettingsRequest(bool IsEnabled);

public sealed record GuideUpdateCheckReport(
    string BaselineRevision,
    string RemoteRevision,
    DateTimeOffset CheckedUtc,
    bool IsComplete,
    IReadOnlyList<GuideUpdateCheckChange> Changes,
    IReadOnlyList<GuideUpdateCheckAddedSource> AddedSources,
    string Summary);

/// <summary>
/// A tracked source definition whose upstream blob was changed or removed.
/// <see cref="IsInUse"/> is calculated from Deluno's saved custom formats, not
/// inferred from an upstream score or template recommendation.
/// </summary>
public sealed record GuideUpdateCheckChange(
    string Kind,
    string Id,
    string Name,
    string MediaType,
    string SourcePath,
    string ChangeType,
    bool IsInUse,
    IReadOnlyList<string> InUseCustomFormatIds);

/// <summary>
/// A new source JSON file in a guide directory Deluno already tracks. It is a
/// review candidate only: no identifier or matcher is assumed until a new
/// pinned package is built and previewed.
/// </summary>
public sealed record GuideUpdateCheckAddedSource(
    string Kind,
    string MediaType,
    string SourcePath);

public static class GuideUpdateCheckStatuses
{
    public const string Disabled = "disabled";
    public const string NeverChecked = "never-checked";
    public const string UpToDate = "up-to-date";
    public const string UpdateAvailable = "update-available";
    public const string Failed = "failed";
}

public interface IGuideUpdateCheckStore
{
    Task<GuideUpdateCheckState> GetAsync(CancellationToken cancellationToken);

    Task<GuideUpdateCheckState> SetEnabledAsync(bool isEnabled, CancellationToken cancellationToken);

    Task<GuideUpdateCheckState> SaveSuccessAsync(
        GuideUpdateCheckReport report,
        CancellationToken cancellationToken);

    Task<GuideUpdateCheckState> SaveFailureAsync(
        string error,
        DateTimeOffset checkedUtc,
        CancellationToken cancellationToken);
}

public interface IGuideUpdateCheckService
{
    Task<GuideUpdateCheckState> GetAsync(CancellationToken cancellationToken);

    Task<GuideUpdateCheckState> SetEnabledAsync(bool isEnabled, CancellationToken cancellationToken);

    /// <summary>Runs an explicit owner-requested check when opt-in is enabled.</summary>
    Task<GuideUpdateCheckState> CheckNowAsync(CancellationToken cancellationToken);

    /// <summary>Runs at most once per interval and only when the owner opted in.</summary>
    Task<GuideUpdateCheckState> RunIfDueAsync(CancellationToken cancellationToken);
}
