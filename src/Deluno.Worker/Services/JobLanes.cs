using Deluno.Contracts;
using Deluno.Jobs.Contracts;

namespace Deluno.Worker.Services;

/// <summary>
/// One lane per kind of work, and nothing shares a timer with anything.
///
/// <para>James: <i>"we need to ensure nothing shares a schedule or timer,
/// everything this app does needs to fire independently when it wants to and
/// when it needs to."</i></para>
///
/// <para><b>What was wrong.</b> Lanes were grouped by the resource they contend
/// on — disk, indexers, remote HTTP — which is a good reason to size them
/// differently and a bad reason to make them queue behind each other. The lease
/// is <c>ORDER BY scheduled_utc</c> across every job type on the lane, so on the
/// two-slot intake lane a backlog of <c>intake.sync</c> starved
/// <c>library.subtitles.search</c> outright: older rows won every time, and
/// subtitles waited on a list sync they had nothing to do with. The same shape
/// sat in <c>import</c>, <c>search.tv</c>, <c>metadata</c> and
/// <c>catalog</c>.</para>
///
/// <para><b>What it costs, measured rather than argued.</b> Twice the lanes
/// polling twice a minute would be twice AUDIT-001's finding 4, so the lanes
/// stopped polling: a lane that leases nothing asks when its <i>own</i> next job
/// is due and sleeps until then, waking early on a signal. That takes an idle
/// executor lane from 120 lease queries an hour to about 24.</para>
///
/// <para>Measured on the lab rig, idle, over two minutes each time:</para>
///
/// <list type="table">
/// <item><term>7 shared lanes, 30s tick</term><description>1.30% CPU, 150&#160;MB</description></item>
/// <item><term>18 lanes, first cut</term><description>1.64% CPU, 161&#160;MB</description></item>
/// <item><term>18 lanes, tuned</term><description><b>0.98% CPU, 139&#160;MB</b></description></item>
/// </list>
///
/// <para>The middle row is the one worth keeping. The query arithmetic said the
/// split would be cheaper and the machine said otherwise — eleven more loops
/// each hold a gate and a scope, and the planning lanes had tripled from one
/// tick to three. Sizing the planners by what they actually plan, and widening
/// the settings cache from one second to fifteen, took it below where it
/// started. Independence ended up cheaper than sharing, but only after it was
/// measured; it was not cheaper by argument.</para>
///
/// <para>The sizes stay resource-shaped, because that part was right: a remote
/// metadata provider still deserves fewer slots than local SQLite. What changed
/// is that nothing waits its turn behind a different kind of work.</para>
///
/// <para>Declared here rather than inside the worker so the rule can be asserted
/// against the list itself. A rule nothing can read is a rule nothing can
/// check.</para>
/// </summary>
public static class JobLanes
{
    /// <summary>
    /// How long an executor lane sleeps when nothing is queued for it and
    /// nothing is scheduled.
    ///
    /// <para>Five minutes rather than thirty seconds, and it costs nothing to be
    /// this patient: the signal covers everything enqueued in-process, and the
    /// due-time sleep covers everything scheduled. What is left is a crashed
    /// worker's abandoned lease, which is not a thing that needs finding inside
    /// half a minute.</para>
    /// </summary>
    public static readonly TimeSpan ExecutorBackstop = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How many jobs a lane runs at once, by what the work is waiting on.
    ///
    /// <para>James: <i>"4 slot lane doesnt sound enough though we need to
    /// maximise this what if someone was a power user and did more with a new
    /// library."</i> Right, and the numbers were worse than small — they were
    /// <b>constants</b>, so a six-core lab box and a thirty-two-core server ran
    /// exactly the same width, and adding cores bought nothing.</para>
    ///
    /// <para><b>Waiting is not working.</b> A search or a metadata refresh spends
    /// almost all its life waiting on somebody else's server, so its width is not
    /// a CPU question and never was: what protects a tracker is
    /// <c>IOutboundRequestThrottle</c>, which paces per <i>host</i> precisely
    /// because two indexer entries can point at one tracker. Narrow lanes never
    /// protected anybody — they only made a queue.</para>
    ///
    /// <para>So network lanes go wide and are held back by the throttle and by
    /// each provider's own rate limit; disk and CPU lanes scale with the machine
    /// and stop there. On the six-core rig that is 12 and 6; on a sixteen-core
    /// desktop, 32 and 16.</para>
    /// </summary>
    private static readonly int Cores = Math.Max(2, Environment.ProcessorCount);

    /// <summary>Local work: SQLite, ffprobe, file moves. Bounded by the machine.</summary>
    private static readonly int LocalWidth = Cores;

    /// <summary>
    /// Remote work: indexers, metadata and subtitle providers. Bounded by the
    /// per-host throttle rather than by cores, so the lane can be wider than the
    /// machine — these threads are asleep on a socket, not busy.
    /// </summary>
    private static readonly int RemoteWidth = Cores * 2;

    // Declared before All, and it has to be: static field initialisers run in
    // declaration order, so a backstop declared after the lane table is still
    // TimeSpan.Zero when the lanes are built — every executor lane would spin
    // instead of sleeping. Caught by JobLaneIndependenceTests the first time it
    // ran, which is the only reason it is not in a release.
    public static readonly IReadOnlyList<JobLane> All =
    [
        // Planning, and three lanes rather than one.
        //
        // Deciding what should run executes nothing itself, so it never sits
        // behind a job — but the three planners used to sit behind *each other*,
        // awaited in sequence on one tick. Import resumption waiting on library
        // automation is the same shape as subtitles waiting on a release search,
        // only smaller, and "smaller" is not a reason to keep it.
        //
        // These keep a fixed tick, and they are the lanes that should: they are
        // not waiting for a queued job, they are deciding whether one should
        // exist, and nothing signals that. Only automation is woken early, by
        // the "planning.wake" sentinel that RequestLibrarySearchAsync sends —
        // the path the Search button takes.
        new("planning.automation", TimeSpan.FromSeconds(30), [],
            PlanAutomation: true,
            SignalTypesOverride: ["planning.wake"]),

        // Picking a paused or interrupted import run back up. Its own pass
        // already claims at fifteen-second and five-minute thresholds, so this
        // is only how often it is offered the chance — and a resumed import is
        // not a thing anybody is watching the clock on.
        new("planning.imports", TimeSpan.FromSeconds(60), [], PlanImports: true),

        // Dispatch cleanup, download retries and metadata top-ups. Each claims
        // its own named pass on its own interval — six hours, two minutes, one
        // minute — so this tick only has to be more frequent than the shortest
        // of them, which is one minute.
        new("planning.maintenance", TimeSpan.FromSeconds(60), [], PlanMaintenance: true),

        // Disk. Sized wide because imports are the backlog users actually feel
        // and the work is mostly waiting on file I/O.
        new("import.files", ["filesystem.import.execute"], BatchSize: LocalWidth * 2, MaxConcurrency: LocalWidth),

        // Each job is one bounded slice of a library scan, so it queues and
        // drains like any other import rather than holding a lease for hours.
        new("import.existing", ["library.import.existing"], BatchSize: LocalWidth, MaxConcurrency: LocalWidth / 2),

        // A directory listing and an ffprobe per file. Disk-shaped like the
        // imports, and now unable to delay one.
        new("subtitles.scan", ["library.subtitles.scan"], BatchSize: LocalWidth, MaxConcurrency: LocalWidth / 2),

        // Reading a file's own streams. Its own lane, not the subtitle scan's,
        // because James asked for passes that do not depend on each other and
        // sharing one would mean a library that wants no subtitles never learns
        // what codec its files are.
        new("media.probe", ["library.media.probe"], BatchSize: LocalWidth, MaxConcurrency: LocalWidth / 2),

        // Timing sync: an FFmpeg pass over a whole audio track, then a
        // correlation. Sized exactly like the scan beside it, because it is the
        // same kind of work — a local process per file, bounded by the machine.
        //
        // It was drafted at half this width, on the reasoning that decoding
        // audio is heavier than probing a container. `Lane_width_scales_with_the
        // _machine` failed it, and the test was right: that reasoning is how
        // every lane in here came to be a small constant, and James had already
        // rejected it — *"4 slot lane doesnt sound enough though we need to
        // maximise this what if someone was a power user."* Audio decode runs at
        // many times real time; three of them on a six-core rig is not a
        // saturated machine, and on a thirty-two-core one it is sixteen.
        //
        // It is emphatically not on `subtitles.search`. That lane exists to
        // spend a provider's daily allowance; parking local CPU work on it is
        // the shape that starved subtitles behind intake in the first place.
        new("subtitles.sync", ["subtitle.sync"], BatchSize: LocalWidth, MaxConcurrency: LocalWidth / 2),

        // Collection membership refreshes are remote metadata work, but they
        // get their own lane so a large collection can never starve a movie
        // library search (or be starved by one). The heartbeat still plans
        // both through the existing automation cycle.
        new("collections.movies", [MovieCollectionJobTypes.Sync], BatchSize: RemoteWidth, MaxConcurrency: RemoteWidth),

        // Indexers, one lane per catalogue so neither can starve the other.
        //
        // Outbound request pacing is handled a layer down: `FeedMediaSearchPlanner`
        // throttles on the *host*, precisely because two indexer entries can point
        // at one tracker. A narrow shared lane never protected a tracker; it only
        // made one catalogue wait for the other.
        new("search.movies", [LibrarySearchJobTypes.Movies], BatchSize: RemoteWidth, MaxConcurrency: RemoteWidth),
        new("search.tv", [LibrarySearchJobTypes.Tv], BatchSize: RemoteWidth, MaxConcurrency: RemoteWidth),

        // The same work at a finer grain, and no longer behind a whole-series
        // search: a season pack search could hold a slot while single episodes
        // queued behind it.
        new("search.episodes", ["episode.search"], BatchSize: RemoteWidth, MaxConcurrency: RemoteWidth),

        // Remote list providers, rate limited by them.
        new("intake", ["intake.sync"], BatchSize: RemoteWidth, MaxConcurrency: RemoteWidth / 2),

        // Subtitle providers. Outbound HTTP to a third party that rate limits
        // us, which is why it used to ride with intake — same resource shape,
        // and two slots between them. A list sync against a slow Trakt could
        // hold both and subtitles simply stopped.
        new("subtitles.search", ["library.subtitles.search"], BatchSize: RemoteWidth, MaxConcurrency: RemoteWidth / 2),

        // Metadata provider HTTP, split per catalogue for the same reason the
        // searches are.
        new("metadata.movies", ["movies.metadata.refresh"], BatchSize: RemoteWidth, MaxConcurrency: RemoteWidth),
        new("metadata.tv", ["series.metadata.refresh"], BatchSize: RemoteWidth, MaxConcurrency: RemoteWidth),

        // Local only: SQLite and CPU, no network. Safe to run wide, and still
        // one lane each — a full catalogue refresh must not hold up the quality
        // recalculation that a single edited profile just triggered.
        new("catalog.movies.quality", ["movies.quality.recalculate"], BatchSize: LocalWidth * 2, MaxConcurrency: LocalWidth),
        new("catalog.tv.quality", ["series.quality.recalculate"], BatchSize: LocalWidth * 2, MaxConcurrency: LocalWidth),
        new("catalog.movies.refresh", ["movies.catalog.refresh"], BatchSize: LocalWidth * 2, MaxConcurrency: LocalWidth),
        new("catalog.tv.refresh", ["series.catalog.refresh"], BatchSize: LocalWidth * 2, MaxConcurrency: LocalWidth)
    ];

}

/// <summary>
/// One lane: one kind of work, its own slots, and its own idea of when to wake.
///
/// <para>Lanes used to be separated by the resource they contend on. They are
/// still <i>sized</i> by it — a remote provider deserves fewer slots than local
/// SQLite — but they are no longer <i>shared</i> by it.</para>
/// </summary>
/// <param name="Interval">
/// The longest a lane will sleep with nothing to do.
///
/// <para><b>A backstop of last resort, not the trigger.</b> A lane is woken by
/// <see cref="IJobLaneSignal"/> the moment matching work is enqueued, and when
/// it finds nothing it sleeps until its <i>own</i> next job is due rather than
/// coming back on a tick. This only covers what neither of those can: lease
/// recovery after a crash, and work written by another process that does not
/// signal.</para>
///
/// <para>Which is why an executor lane does not set one and gets
/// <see cref="JobLanes.ExecutorBackstop"/>. A planning lane does set one,
/// because it is not waiting for a queued job — it is deciding whether one
/// should exist, and nothing signals that.</para>
/// </param>
/// <param name="JobTypes">
/// Empty means the lane only plans work and never executes it.
/// </param>
/// <param name="BatchSize">Jobs claimed per tick.</param>
/// <param name="MaxConcurrency">Jobs from that batch run at once.</param>
/// <param name="Enabled">Whether this lane starts at all.</param>
/// <param name="JitterOverride">
/// A random delay up to this length applied once before the lane's first tick,
/// so lanes do not all wake and hit SQLite in the same instant. Defaults to 25%
/// of <see cref="Interval"/>.
/// </param>
/// <param name="SignalTypesOverride">
/// The job types this lane registers with <see cref="IJobLaneSignal"/> to be
/// woken by. Defaults to <see cref="JobTypes"/>; a planning-only lane needs an
/// explicit override, since it executes no job type but still wants to be
/// signalled.
/// </param>
public sealed record JobLane(
    string Name,
    TimeSpan Interval,
    IReadOnlyList<string> JobTypes,
    bool PlanAutomation = false,
    bool PlanImports = false,
    bool PlanMaintenance = false,
    int BatchSize = 8,
    int MaxConcurrency = 4,
    bool Enabled = true,
    TimeSpan? JitterOverride = null,
    IReadOnlyList<string>? SignalTypesOverride = null)
{
    /// <summary>
    /// A random delay before this lane's first tick, so lanes do not all wake
    /// and hit SQLite in the same instant.
    ///
    /// <para><b>Capped at two seconds.</b> A quarter of the interval was fine
    /// when every interval was thirty seconds; against a five-minute backstop it
    /// became seventy-five seconds of a lane not existing yet — and a signal
    /// arriving in that window is dropped, because the lane has not registered
    /// for it. Spreading eighteen lanes over two seconds is all the herd needs
    /// spreading.</para>
    /// </summary>
    public TimeSpan Jitter { get; init; } = JitterOverride ?? TimeSpan.FromMilliseconds(
        Math.Min(Interval.TotalMilliseconds * 0.25, 2_000));

    public IReadOnlyList<string> SignalTypes { get; init; } = SignalTypesOverride ?? JobTypes;

    /// <summary>An executor lane: its own job type, its own slots, no tick.</summary>
    public JobLane(string Name, IReadOnlyList<string> JobTypes, int BatchSize, int MaxConcurrency)
        : this(Name, JobLanes.ExecutorBackstop, JobTypes, BatchSize: BatchSize, MaxConcurrency: MaxConcurrency)
    {
    }
}
