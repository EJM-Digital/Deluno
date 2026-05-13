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
    IReadOnlyList<string> Notes);

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
