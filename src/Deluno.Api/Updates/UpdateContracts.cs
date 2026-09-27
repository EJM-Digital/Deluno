namespace Deluno.Api.Updates;

public static class UpdateModes
{
    public const string NotifyOnly = "notify-only";
    public const string DownloadBackground = "download-background";
    public const string DownloadApplyOnRestart = "download-apply-on-restart";

    public static bool IsValid(string? value)
    {
        return value is NotifyOnly or DownloadBackground or DownloadApplyOnRestart;
    }
}

public static class UpdateInstallKinds
{
    public const string WindowsPackaged = "windows-packaged";
    public const string Docker = "docker";
    public const string Manual = "manual";
}

public static class UpdateStates
{
    public const string Idle = "idle";
    public const string Checking = "checking";
    public const string UpdateAvailable = "update-available";
    public const string Downloading = "downloading";
    public const string ReadyToRestart = "ready-to-restart";
    public const string UpToDate = "up-to-date";
    public const string NotSupported = "not-supported";
    public const string Error = "error";
}

public sealed record UpdateStatusResponse(
    string CurrentVersion,
    string Channel,
    string InstallKind,
    string BehaviorMode,
    bool IsInstalled,
    bool CanCheck,
    bool CanDownload,
    bool CanApply,
    bool UpdateAvailable,
    string? LatestVersion,
    string State,
    int? ProgressPercent,
    bool RestartRequired,
    DateTimeOffset? LastCheckedUtc,
    DateTimeOffset? LastDownloadedUtc,
    string Message,
    string? LastError,
    IReadOnlyList<string> Notes,
    string? CurrentImageRef = null,
    string? CurrentImageDigest = null,
    // Notes for a PENDING update (LatestVersion), looked up the same way as
    // AppliedUpdateEntry.Notes once a version to look up exists. There is no
    // update checker yet (every install kind reports UpdateAvailable=false
    // and LatestVersion=null - see DefaultUpdateOrchestrator.BuildStatus), so
    // this always stays null today; that is not a bug or an omission on
    // screen. When a checker exists, set it from
    // IReleaseNotesProvider.GetNotes(LatestVersion) the same way applied
    // entries get theirs.
    string? Changelog = null,
    IReadOnlyList<AppliedUpdateEntry>? AppliedUpdates = null);

/// <summary>
/// One entry in the "past 5 updates applied" history (#870), recorded the
/// moment Deluno notices its own running version changed from what it last
/// recorded — the same signal that fires the "On Application Update"
/// outbound notification (#856).
/// </summary>
/// <param name="Notes">
/// This version's plain-language release notes (RELEASE_NOTES.md via
/// IReleaseNotesProvider), or null when the notes file has no section for
/// it. Looked up fresh each time the status is built, not persisted to
/// update-history.json, so editing the notes file takes effect immediately
/// without rewriting history already on disk.
/// </param>
public sealed record AppliedUpdateEntry(string Version, DateTimeOffset AppliedAtUtc, string? Notes = null);

public sealed record UpdatePreferencesResponse(
    string Mode,
    string Channel,
    bool AutoCheck);

public sealed record UpdatePreferencesRequest(
    string? Mode,
    string? Channel,
    bool? AutoCheck);

public sealed record UpdateActionResponse(
    bool Accepted,
    string Message,
    UpdateStatusResponse Status);
