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
/// <param name="IntervalSeconds">
/// The cadence the pass was last claimed at, which for a configurable pass is
/// the user's choice rather than the declared default. Null until it has run
/// once, in which case the declaration is still the best answer available.
/// </param>
public sealed record SystemTaskState(
    string ScheduleKey,
    DateTimeOffset? LastStartedUtc,
    DateTimeOffset? LastCompletedUtc,
    string? LastResult,
    long? LastDurationMs,
    DateTimeOffset? NextRunUtc,
    long? IntervalSeconds = null,
    string? LastError = null);

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
    public const string LibraryFileCheck = "library.file.check";
    public const string BlockedReleaseCleanup = "blocked.release.cleanup";
    public const string ProcessorHealth = "processor.health";
    public const string ProcessorOutcomes = "processor.outcomes";
    public const string RssSync = "rss.sync";
    public const string HealthChecks = "health.checks";
    public const string ConnectionsCheck = "connections.check";
    public const string SceneMappingRefresh = "scene.mapping.refresh";
    public const string EpisodeGroupRefresh = "episode.group.refresh";
    public const string DownloadLeftoverCleanup = "download.leftover.cleanup";
    public const string UnmatchedImportGiveUp = "unmatched.import.giveUp";
    public const string DatabaseIntegrity = "database.integrity";

    /// <summary>
    /// Ordered as somebody reading the screen would want them: the things that
    /// keep a download moving first, then the things that keep the library
    /// true, then housekeeping.
    /// </summary>
    public static readonly IReadOnlyList<SystemTask> All =
    [
        // Fifteen minutes, as Radarr and Sonarr read feeds. A feed read costs
        // one request per indexer and finds a new release the moment it is
        // listed, where the search cycle finds it on the title's turn (#587).
        new(RssSync, "Watch indexers for new releases",
            "Reads each indexer's newest releases and searches straight away for any title you want, instead of waiting for its turn in the search cycle.",
            TimeSpan.FromMinutes(15)),
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
        // No "Retry failed grabs" pass. One stood here and never retried
        // anything: nothing opened the window it waited for. A title whose
        // grab failed is searched again on its library's schedule (#621).
        // Cannot happen at the moment of blocking: the sharing rule may still
        // own that copy, and it is the thing that knows how long the site
        // expects you to keep seeding. DESIGN-007 decisions 16 and 17.
        new(BlockedReleaseCleanup, "Clear refused downloads",
            "Removes the leftovers of releases you have blocked, once your sharing rule no longer needs them.",
            TimeSpan.FromMinutes(5)),
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
        // Deluno never looked at a file again after importing it, so a film you
        // deleted yourself still showed as held, was never searched for, and
        // answered "you already have this" when asked why it would not
        // download. Three wrong answers, all sounding certain. DESIGN-007
        // decisions 11 and 18.
        new(LibraryFileCheck, "Check library files",
            "Looks at whether the files Deluno thinks you have are still on disk, and marks anything that has gone as missing.",
            TimeSpan.FromHours(6),
            IsConfigurable: true),
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
            "Clears finished download records and year-old completed jobs that nothing needs any more.",
            TimeSpan.FromHours(6)),
        // A minute, whether or not a hand-off is waiting (#1227): with none in flight this pass is the only thing that
        // hears a processor stop, and five minutes left the Dashboard, System and the tray green for two to four.
        new(ProcessorHealth, "Check the processor",
            "Asks each processor whether it is answering, so one that stops is noticed within a minute, before a file is stuck behind it.",
            TimeSpan.FromMinutes(1)),
        // A minute, because each report carries its own next attempt and the
        // pass sends only what is due. A settled install sends nothing, so the
        // pass costs a query (#580).
        new(ProcessorOutcomes, "Tell the processor what became of its files",
            "Says again what became of a processed file when the processor did not hear it the first time, so it can let go of its copy.",
            TimeSpan.FromMinutes(1)),
        // Every health check, on one clock (#909). A setting a check depends on
        // (a library's root, a download client, a processor connection) also
        // asks its checks to run right away, so this fifteen minutes is the
        // worst case, not the only case.
        new(HealthChecks, "Check system health",
            "Looks at download clients, indexers, libraries, the processor and other connections for problems, and shows anything wrong on Health, the Dashboard, the bell and a notification.",
            TimeSpan.FromMinutes(15)),
        // One minute is how often Deluno looks, not how often it tests: a connection is tested for real only
        // when nothing has used or checked it for fifteen minutes (#1098). It only reads, so it is on for every
        // install; the setting Check connections regularly turns it off.
        new(ConnectionsCheck, "Test connections",
            "Tests any indexer, download client, processor, media server, Seerr, metadata service or list that nothing has used for 15 minutes, so Checked on System is always true.",
            TimeSpan.FromMinutes(1)),
        // Twelve hours: a scene mapping does not change mid-day, and every
        // anime title is asked for on its own request, so this is paced for
        // TheXEM's courtesy rather than for freshness (#863).
        new(SceneMappingRefresh, "Refresh anime scene numbering",
            "Checks TheXEM for scene-numbering corrections on your anime titles, so a release named by fansub order still matches the right episode.",
            TimeSpan.FromHours(12)),
        // Once a day: TMDb's episode groups (DVD order, absolute numbering,
        // story arcs) change about as often as a physical release schedule
        // does, which is to say rarely, and every title is asked for on its
        // own request (#863).
        new(EpisodeGroupRefresh, "Refresh episode ordering options",
            "Checks TMDb for DVD-order and absolute-numbering episode groups on your titles, so the numbering switcher only offers orderings a title actually has.",
            TimeSpan.FromHours(24)),
        // #932 (cleanup sheet row M1): off by default, per #936's later,
        // standing rule - this is one of the setup guide's automation
        // questions now, the same as every other automatic behaviour, not
        // the "on" the cleanup sheet's own row first asked for. The pass
        // itself still runs hourly regardless (the same cadence as
        // dispatch-recovery polling); the service reads the switch and does
        // nothing while it is off.
        new(DownloadLeftoverCleanup, "Clean up leftover downloads",
            "Deletes a download-folder leftover nothing is using once it is old enough, with the reason written to Activity.",
            TimeSpan.FromHours(1),
            IsConfigurable: true),
        // #888: only ever does anything when "give up after N days" is the
        // chosen unmatched-import policy - hourly is cheap insurance against
        // a case sitting a day past its limit before anyone notices.
        new(UnmatchedImportGiveUp, "Give up on old unmatched imports",
            "When you chose 'give up after N days', removes, deletes and blocklists a file Deluno still could not match once that many days have passed.",
            TimeSpan.FromHours(1)),
        new(DatabaseIntegrity, "Check the databases",
            "Asks each of Deluno's databases whether its file is sound, once a day, and notes the result on System.",
            TimeSpan.FromDays(1))
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
