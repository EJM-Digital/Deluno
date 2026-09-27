namespace Deluno.Integrations.Search.Trackers;

/// <summary>
/// The owner-controlled state for the scheduled tracker-definition update job
/// (#756): whether it is on, how often it runs, and what its last pass did.
/// Writing a fetched definition into the data folder's <c>trackers</c>
/// directory is the same override TrackerDefinitionCatalog already treats a
/// hand-dropped YAML file as -- this only automates dropping it there.
/// </summary>
public sealed record TrackerDefinitionUpdateState(
    bool IsEnabled,
    int IntervalHours,
    DateTimeOffset? LastCheckedUtc,
    DateTimeOffset? LastUpdatedUtc,
    string Status,
    string? Error,
    IReadOnlyList<string> UpdatedTrackerIds,
    DateTimeOffset UpdatedUtc);

public sealed record UpdateTrackerDefinitionUpdateSettingsRequest(bool IsEnabled, int IntervalHours);

public static class TrackerDefinitionUpdateStatuses
{
    public const string Disabled = "disabled";
    public const string NeverChecked = "never-checked";
    public const string UpToDate = "up-to-date";
    public const string Updated = "updated";
    public const string Failed = "failed";
}

/// <summary>One tracker definition as the remote source has it, keyed by the same id TrackerDefinitionCatalog uses.</summary>
public sealed record RemoteTrackerDefinition(string Id, string Yaml);

/// <summary>Fetches the current set of tracker definitions from wherever Deluno is configured to look.</summary>
public interface IRemoteTrackerDefinitionSource
{
    Task<IReadOnlyList<RemoteTrackerDefinition>> FetchAsync(CancellationToken cancellationToken);
}

public interface ITrackerDefinitionUpdateStore
{
    Task<TrackerDefinitionUpdateState> GetAsync(CancellationToken cancellationToken);

    Task<TrackerDefinitionUpdateState> SetSettingsAsync(bool isEnabled, int intervalHours, CancellationToken cancellationToken);

    Task<TrackerDefinitionUpdateState> SaveSuccessAsync(
        DateTimeOffset checkedUtc,
        IReadOnlyList<string> updatedTrackerIds,
        CancellationToken cancellationToken);

    Task<TrackerDefinitionUpdateState> SaveFailureAsync(
        string error,
        DateTimeOffset checkedUtc,
        CancellationToken cancellationToken);
}

public interface ITrackerDefinitionUpdateService
{
    Task<TrackerDefinitionUpdateState> GetAsync(CancellationToken cancellationToken);

    Task<TrackerDefinitionUpdateState> SetSettingsAsync(bool isEnabled, int intervalHours, CancellationToken cancellationToken);

    /// <summary>Runs an explicit owner-requested update regardless of the scheduled on/off switch -- the "Update now" button.</summary>
    Task<TrackerDefinitionUpdateState> UpdateNowAsync(CancellationToken cancellationToken);

    /// <summary>Runs at most once per configured interval, and only when the owner turned the scheduled job on.</summary>
    Task<TrackerDefinitionUpdateState> RunIfDueAsync(CancellationToken cancellationToken);
}
