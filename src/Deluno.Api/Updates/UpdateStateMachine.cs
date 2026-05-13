namespace Deluno.Api.Updates;

public sealed class UpdateStateMachine
{
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
    }

    public void MarkChecking()
    {
        State = UpdateStates.Checking;
        ProgressPercent = null;
        LastError = null;
    }

    public void MarkUpToDate(DateTimeOffset checkedUtc, bool restartRequired)
    {
        LastCheckedUtc = checkedUtc;
        RestartRequired = restartRequired;
        UpdateAvailable = restartRequired;
        LatestVersion = restartRequired ? LatestVersion : null;
        State = restartRequired ? UpdateStates.ReadyToRestart : UpdateStates.UpToDate;
        ProgressPercent = restartRequired ? 100 : null;
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
    }

    public void MarkDownloading()
    {
        State = UpdateStates.Downloading;
        ProgressPercent = 0;
        LastError = null;
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
    }

    public void MarkReadyToRestart()
    {
        RestartRequired = true;
        UpdateAvailable = true;
        State = UpdateStates.ReadyToRestart;
    }

    public void MarkError(string? message)
    {
        LastError = message;
        State = UpdateStates.Error;
    }
}
