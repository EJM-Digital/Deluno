using Deluno.Api.Updates;

namespace Deluno.Tray;

/// <summary>
/// The one rule for whether Deluno fetches a found update by itself, shared by the thing that downloads
/// (VelopackUpdateOrchestrator) and the thing that announces (TrayApplication), so the two cannot disagree (#1367).
/// </summary>
public static class UpdatePolicy
{
    /// <summary>True only when automatic checking is on and the mode downloads; otherwise a found update waits for the person.</summary>
    public static bool DownloadsByItself(AppSettings settings)
        => settings.AutoCheckUpdates && settings.UpdateMode != UpdateModes.NotifyOnly;

    /// <summary>
    /// Whether the "available" or "ready" balloon is shown now. "Ready" always is; "available" is skipped only when Deluno
    /// is about to download it by itself, because the "ready" balloon follows.
    /// </summary>
    public static bool ShouldAnnounce(TrayUpdate update, AppSettings settings)
        => update.HasUpdate
           && !(update.Action == UpdateMenuAction.Download && DownloadsByItself(settings));
}
