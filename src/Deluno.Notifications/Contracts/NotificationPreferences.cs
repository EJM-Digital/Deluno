namespace Deluno.Notifications.Contracts;

public class NotificationPreferences
{
    public bool ImportCompletedEnabled { get; set; } = true;
    public bool ImportFailedEnabled { get; set; } = true;

    /// <summary>
    /// Covers both "health.degraded" and "health.restored" for every
    /// provider type (indexers, download clients, media servers,
    /// processors) with one decision, matching Deluno's single-user,
    /// one-decision-per-concept style rather than per-provider-type
    /// toggles (#859).
    /// </summary>
    public bool ProviderHealthChangedEnabled { get; set; } = true;


    // Lifecycle triggers beyond import-failed (#856). None of these fired
    // outbound before, so each defaults off -- nothing new fires unannounced.
    public bool UpgradeEnabled { get; set; } = false;
    public bool RenameEnabled { get; set; } = false;
    public bool FileDeletedEnabled { get; set; } = false;
    public bool ManualInteractionRequiredEnabled { get; set; } = false;
    public bool GrabEnabled { get; set; } = false;
    public bool AddedEnabled { get; set; } = false;
    public bool UpdateEnabled { get; set; } = false;

    // Deluno's own recovery/cleanup actions (#858): each already lands in
    // Activity; these gate the same actions reaching the outbound path too.
    // Off by default, matching the conservative default for every other
    // automation-triggered toggle.
    public bool StalledDownloadHandledEnabled { get; set; } = false;
    public bool QueueItemRemovedEnabled { get; set; } = false;
    public bool OrphanedDownloadRemovedEnabled { get; set; } = false;

    // #966: the same recovery/cleanup shape, for the actions #858 missed --
    // each already lands in Activity ("acquisition.override",
    // "download.health.strikes-purged", "download.cleanup.cleared",
    // "filesystem.import.unmatched-given-up"). Off by default (#936).
    public bool ForcedImportEnabled { get; set; } = false;
    public bool StrikesPurgedEnabled { get; set; } = false;
    public bool CleanupClearedEnabled { get; set; } = false;
    public bool UnmatchedImportGivenUpEnabled { get; set; } = false;
}
