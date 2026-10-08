namespace Deluno.Api.Updates;

public sealed class UpdateStateMachine
{
    /// <summary>
    /// Raised after the state changes (not on every progress tick), so a listener such as the Windows tray icon
    /// follows the update without polling.
    /// </summary>
    public event Action? Changed;

    public string State { get; private set; } = UpdateStates.Idle;
    public string? LatestVersion { get; private set; }
    public string? LastError { get; private set; }
    public int? ProgressPercent { get; private set; }
    public DateTimeOffset? LastCheckedUtc { get; private set; }
    public DateTimeOffset? LastDownloadedUtc { get; private set; }
    public bool RestartRequired { get; private set; }
    public bool UpdateAvailable { get; private set; }

    public void MarkNotSupported()
    {
        State = UpdateStates.NotSupported;
        Changed?.Invoke();
    }

    public void MarkChecking()
    {
        State = UpdateStates.Checking;
        ProgressPercent = null;
        LastError = null;
        Changed?.Invoke();
    }

    public void MarkUpToDate(DateTimeOffset checkedUtc, bool restartRequired)
    {
        LastCheckedUtc = checkedUtc;
        RestartRequired = restartRequired;
        UpdateAvailable = restartRequired;
        LatestVersion = restartRequired ? LatestVersion : null;
        State = restartRequired ? UpdateStates.ReadyToRestart : UpdateStates.UpToDate;
        ProgressPercent = restartRequired ? 100 : null;
        Changed?.Invoke();
    }

    public void MarkUpdateAvailable(DateTimeOffset checkedUtc, string latestVersion)
    {
        LastCheckedUtc = checkedUtc;
        LatestVersion = latestVersion;
        UpdateAvailable = true;
        RestartRequired = false;
        State = UpdateStates.UpdateAvailable;
        ProgressPercent = null;
        LastError = null;
        Changed?.Invoke();
    }

    public void MarkDownloading()
    {
        State = UpdateStates.Downloading;
        ProgressPercent = 0;
        LastError = null;
        Changed?.Invoke();
    }

    public void ReportProgress(int progressPercent)
    {
        ProgressPercent = Math.Clamp(progressPercent, 0, 100);
    }

    public void MarkDownloaded(DateTimeOffset downloadedUtc, bool restartRequired = true)
    {
        LastDownloadedUtc = downloadedUtc;
        RestartRequired = restartRequired;
        UpdateAvailable = restartRequired;
        ProgressPercent = 100;
        State = restartRequired ? UpdateStates.ReadyToRestart : UpdateStates.UpToDate;
        Changed?.Invoke();
    }

    public void MarkReadyToRestart()
    {
        RestartRequired = true;
        UpdateAvailable = true;
        State = UpdateStates.ReadyToRestart;
        Changed?.Invoke();
    }

    public void MarkError(string? message)
    {
        LastError = message;
        State = UpdateStates.Error;
        Changed?.Invoke();
    }
}
