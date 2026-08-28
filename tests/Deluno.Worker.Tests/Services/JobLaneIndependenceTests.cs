using Deluno.Worker.Services;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// Nothing shares a schedule or a timer.
///
/// <para>James: <i>"we need to ensure nothing shares a schedule or timer,
/// everything this app does needs to fire independently when it wants to and
/// when it needs to."</i> These are the assertions that keep it true, because
/// the failure is silent: two job types on one lane look fine until one of them
/// is starved, and the starved one is whichever was enqueued second.</para>
/// </summary>
public sealed class JobLaneIndependenceTests
{
    private static IReadOnlyList<JobLane> Executors =>
        [.. JobLanes.All.Where(lane => lane.JobTypes.Count > 0)];

    /// <summary>
    /// The rule itself. The lease is <c>ORDER BY scheduled_utc</c> across a
    /// lane's job types, so two types on one lane means the older one always
    /// wins — which is starvation, not scheduling.
    /// </summary>
    [Fact]
    public void No_two_kinds_of_work_share_a_lane()
    {
        var sharing = Executors
            .Where(lane => lane.JobTypes.Count > 1)
            .Select(lane => $"{lane.Name}: {string.Join(", ", lane.JobTypes)}")
            .ToArray();

        Assert.Empty(sharing);
    }

    /// <summary>And no kind of work is spread across two lanes either, which
    /// would give it two independent sets of slots and no way to reason about
    /// its concurrency.</summary>
    [Fact]
    public void No_kind_of_work_is_claimed_by_two_lanes()
    {
        var duplicated = Executors
            .SelectMany(lane => lane.JobTypes)
            .GroupBy(jobType => jobType, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        Assert.Empty(duplicated);
    }

    /// <summary>
    /// An executor lane waits on its own next job, not on a tick.
    ///
    /// <para>A short interval here would quietly reintroduce polling: the lane
    /// would wake, find nothing and query again, which is what the due-time
    /// sleep replaced.</para>
    /// </summary>
    [Fact]
    public void An_executor_lane_waits_on_its_own_work_rather_than_a_tick()
    {
        foreach (var lane in Executors)
        {
            Assert.Equal(JobLanes.ExecutorBackstop, lane.Interval);
        }
    }

    /// <summary>
    /// Only the planning lanes keep a tick, and they are meant to: they are not
    /// waiting for a queued job, they are deciding whether one should exist, and
    /// nothing signals that.
    /// </summary>
    [Fact]
    public void Only_planning_lanes_keep_a_tick()
    {
        foreach (var lane in JobLanes.All.Where(lane => lane.Interval < JobLanes.ExecutorBackstop))
        {
            Assert.StartsWith("planning", lane.Name, StringComparison.Ordinal);
            Assert.Empty(lane.JobTypes);
        }
    }

    /// <summary>
    /// And each planner is its own lane. They used to be awaited in sequence on
    /// one tick, so import resumption waited on library automation — the same
    /// shape as subtitles waiting on a release search, only smaller.
    /// </summary>
    [Fact]
    public void No_two_planners_share_a_tick()
    {
        Assert.Single(JobLanes.All, lane => lane.PlanAutomation);
        Assert.Single(JobLanes.All, lane => lane.PlanImports);
        Assert.Single(JobLanes.All, lane => lane.PlanMaintenance);

        foreach (var lane in JobLanes.All)
        {
            var planners = (lane.PlanAutomation ? 1 : 0) + (lane.PlanImports ? 1 : 0) + (lane.PlanMaintenance ? 1 : 0);
            Assert.True(planners <= 1, $"{lane.Name} plans {planners} things on one tick.");
        }
    }

    /// <summary>
    /// An idle executor lane must not poll.
    ///
    /// <para>AUDIT-001 finding 4 is that idle database round trips are a defect
    /// in their own right, and fifteen lanes leasing every thirty seconds would
    /// have been twice the seven that finding was written about. So they sleep
    /// on their own next due time instead.</para>
    ///
    /// <para><b>This is the query count, not the whole cost.</b> Idle CPU on the
    /// rig went 1.30% → 1.64% → 0.98% across the shared lanes, the first cut of
    /// the split, and the tuned version. The middle number is the point: the
    /// arithmetic said cheaper and the machine disagreed until the planning
    /// ticks and the settings cache were fixed. Do not quote this test as proof
    /// the split was free — quote the rig.</para>
    /// </summary>
    [Fact]
    public void An_idle_executor_lane_does_not_poll()
    {
        // Two queries — a lease that finds nothing, then "when could there be
        // something" — once per backstop, rather than a lease every tick.
        var queriesPerHour = Executors.Count * (3600 / JobLanes.ExecutorBackstop.TotalSeconds) * 2;

        // What seven lanes on a thirty-second tick used to cost, as the yardstick.
        const double before = 7 * (3600d / 30) * 1;

        Assert.True(
            queriesPerHour < before,
            $"Idle queries an hour: {queriesPerHour} against {before} before the split.");
    }

    /// <summary>
    /// Width follows the machine, not a constant.
    ///
    /// <para>James: <i>"4 slot lane doesnt sound enough though we need to
    /// maximise this what if someone was a power user."</i> The old numbers were
    /// hard-coded, so a six-core lab box and a thirty-two-core server ran exactly
    /// the same width and adding cores bought nothing.</para>
    /// </summary>
    [Fact]
    public void Lane_width_scales_with_the_machine()
    {
        var cores = Math.Max(2, Environment.ProcessorCount);

        // No executor lane is narrower than half the cores — the old constants
        // were 2 and 4 regardless of the box.
        foreach (var lane in Executors)
        {
            Assert.True(
                lane.MaxConcurrency >= cores / 2,
                $"{lane.Name} runs {lane.MaxConcurrency} at once on a {cores}-core machine.");
        }

        // And the ones waiting on somebody else's server go wider than the
        // machine, because a thread asleep on a socket is not using a core.
        var remote = Executors.Single(lane => lane.Name == "search.movies");
        Assert.True(remote.MaxConcurrency > cores, "A network lane should not be capped at core count.");
    }

    /// <summary>
    /// Every lane says what it is for. A lane named after a resource rather than
    /// a kind of work is how two kinds ended up sharing one in the first place.
    /// </summary>
    [Fact]
    public void Every_lane_is_named_and_sized()
    {
        foreach (var lane in JobLanes.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(lane.Name));
            Assert.True(lane.MaxConcurrency > 0, $"{lane.Name} has no slots.");
            Assert.True(lane.BatchSize >= lane.MaxConcurrency, $"{lane.Name} cannot fill its own slots.");
        }
    }
}
