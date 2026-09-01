namespace Deluno.Contracts;

/// <summary>
/// One recurring pass Deluno runs on its own, declared where a person can see
/// it.
/// </summary>
/// <param name="Key">
/// What the scheduler claims the pass under. Persisted, so it is renamed at the
/// cost of the pass running once more than it needed to — never silently.
/// </param>
/// <param name="Name">What it is called on the System screen.</param>
/// <param name="Description">
/// What it actually does, in a sentence, in the words a person would use. Not
/// the class name.
/// </param>
public sealed record SystemTask(
    string Key,
    string Name,
    string Description,
    TimeSpan Interval,
    bool IsConfigurable = false);

/// <summary>
/// The persisted run state for one recurring pass. The schedule definition is
/// deliberately kept separate from this state so fixed engineering cadences
/// and user-configured cadences can be displayed without pretending they are
/// the same thing.
/// </summary>
public sealed record SystemTaskState(
    string ScheduleKey,
    DateTimeOffset? LastStartedUtc,
    DateTimeOffset? LastCompletedUtc,
    string? LastResult,
    long? LastDurationMs,
    DateTimeOffset? NextRunUtc);

/// <summary>
/// Every scheduled pass, in one place, on a fixed interval.
///
/// <para><b>Why this exists.</b> The intervals were written at their call
/// sites — eight <c>TryClaimScheduledPassAsync("download.state",
/// TimeSpan.FromMinutes(5), …)</c> scattered through the planner. Nothing could
/// list them, so there was no way to show somebody what Deluno runs, when it
/// last ran, or when it runs next, and each new pass was another line buried in
/// a method. James: <i>"all these scheduled jobs again should system jobs the
/// same as what radarr does, we keep them in one spot and they fire on a
/// specific unconfigurable schedule right??"</i></para>
///
/// <para><b>Fixed, not configurable</b>, exactly as Radarr's are. These
/// intervals are engineering decisions — how often it is worth asking a
/// download client what it is doing, how long a metadata answer stays true —
/// and not preferences. A library's own automation cadence <i>is</i>
/// configurable and lives on the library, which is a different question.</para>
///
/// <para><b>One scheduler, still.</b> This is a list of names and intervals, not
/// a second timer: every pass is claimed on the existing heartbeat through
/// <c>TryClaimScheduledPassAsync</c> (DESIGN-002 rule 3). Nothing here starts
/// anything.</para>
/// </summary>
public static class SystemTasks
{
    public const string DispatchCleanup = "dispatch.cleanup";
    public const string DownloadState = "download.state";
    public const string DispatchRetry = "dispatch.retry";
    public const string MetadataRefresh = "metadata.refresh";
    public const string IntakeAutomation = "intake.automation";
    public const string LibraryImportResume = "library.import.resume";
    public const string SharingReclaim = "sharing.reclaim";
    public const string ImportAutomation = "import.automation";
    public const string MediaProbe = "media.probe";
    public const string ArtworkCacheCleanup = "artwork.cache.cleanup";
    public const string Backup = "backup.schedule";
    public const string RankingModelTraining = "ranking.model.training";
    public const string ImportRecoveryRetention = "import.recovery.retention";
    public const string DownloadDispatchPolling = "dispatch.polling";
    public const string RecycleBinCleanup = "recycle.bin.cleanup";

    /// <summary>
    /// Ordered as somebody reading the screen would want them: the things that
    /// keep a download moving first, then the things that keep the library
    /// true, then housekeeping.
    /// </summary>
    public static readonly IReadOnlyList<SystemTask> All =
    [
        new(ImportAutomation, "Import finished downloads",
            "Moves anything a download client has finished into your library.",
            TimeSpan.FromSeconds(15)),
        new(SharingReclaim, "Reclaim seeding files",
            "Frees files a tracker no longer needs you to seed.",
            TimeSpan.FromSeconds(30)),
        new(LibraryImportResume, "Resume library scans",
            "Picks up a library scan that was interrupted.",
            TimeSpan.FromMinutes(1)),
        // A minute, and that is not a typo. It tops the metadata queue up to a
        // target depth rather than queueing a fixed number per pass, so a
        // freshly imported library drains continuously instead of taking 167
        // days at thirty titles every six hours. A settled library queues
        // nothing, so the pass costs a query.
        new(MetadataRefresh, "Refresh metadata",
            "Tops up artwork, ratings and details for titles whose information has gone stale.",
            TimeSpan.FromMinutes(1)),
        new(DispatchRetry, "Retry failed grabs",
            "Tries again on releases that failed to reach a download client.",
            TimeSpan.FromMinutes(2)),
        new(DownloadState, "Reconcile downloads",
            "Asks each download client what it is actually working on, and clears anything it has forgotten.",
            TimeSpan.FromMinutes(5)),
        new(IntakeAutomation, "Check lists",
            "Reads the lists you follow and adds anything new.",
            TimeSpan.FromMinutes(5)),
        new(MediaProbe, "Read media files",
            "Reads the codec, audio and channel layout out of the files you hold, for the ones whose names do not say.",
            TimeSpan.FromMinutes(30)),
        new(ArtworkCacheCleanup, "Clean cached artwork",
            "Removes artwork no movie or show still references after a safety window.",
            TimeSpan.FromHours(6)),
        new(Backup, "Create scheduled backups",
            "Creates a backup when the backup schedule says one is due.",
            TimeSpan.FromDays(1),
            IsConfigurable: true),
        new(RankingModelTraining, "Train release ranking",
            "Learns from recorded release outcomes when ranking training is enabled.",
            TimeSpan.FromDays(1),
            IsConfigurable: true),
        new(ImportRecoveryRetention, "Expire recovery cases",
            "Removes resolved import recovery cases older than the configured retention.",
            TimeSpan.FromDays(1),
            IsConfigurable: true),
        new(DownloadDispatchPolling, "Poll dispatch recovery",
            "Checks unresolved downloads for grab, detection and import timeouts.",
            TimeSpan.FromHours(1)),
        new(RecycleBinCleanup, "Clean the recycle bin",
            "Removes library files whose recycle-bin retention has expired.",
            TimeSpan.FromHours(6),
            IsConfigurable: true),
        new(DispatchCleanup, "Housekeeping",
            "Clears finished dispatch records that nothing needs any more.",
            TimeSpan.FromHours(6))
    ];

    /// <summary>
    /// The interval for a pass, by key.
    /// </summary>
    /// <exception cref="KeyNotFoundException">
    /// Deliberately loud. A pass asking for an interval it never declared is a
    /// pass nothing can show on the System screen, and returning a plausible
    /// default would hide exactly that.
    /// </exception>
    public static TimeSpan IntervalFor(string key)
        => All.FirstOrDefault(task => task.Key == key)?.Interval
           ?? throw new KeyNotFoundException(
               $"'{key}' is not a declared system task. Add it to SystemTasks.All so it can be shown and scheduled.");

    /// <summary>
    /// Resolves a user-configured hourly cadence while still requiring the
    /// pass to be declared in the registry. Configuration belongs at the
    /// settings boundary; hosted services must not invent an invisible task.
    /// </summary>
    public static TimeSpan IntervalForHours(string key, int hours)
    {
        var task = All.FirstOrDefault(item => item.Key == key)
            ?? throw new KeyNotFoundException(
                $"'{key}' is not a declared system task. Add it to SystemTasks.All so it can be shown and scheduled.");

        if (!task.IsConfigurable)
        {
            throw new InvalidOperationException($"'{key}' is not a configurable system task.");
        }

        return TimeSpan.FromHours(Math.Clamp(hours, 1, 168));
    }
}
