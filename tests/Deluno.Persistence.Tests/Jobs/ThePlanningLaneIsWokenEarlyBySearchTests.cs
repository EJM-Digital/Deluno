using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Worker.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// Coverage register, area services: <c>planning.wake</c> is a signal sentinel, not a job. A person pressing Search on a library sends it
/// (once, and only when the request is accepted), and the automation planning lane is the lane declared to wake on it, so the planner
/// looks at once instead of waiting out its thirty-second tick. Real job store on a migrated database.
/// </summary>
public sealed class ThePlanningLaneIsWokenEarlyBySearchTests
{
    private static LibraryAutomationPlanItem Plan(string id)
        => new(id, "Films", "movies", true, true, true, SearchIntervalMinutes: 360, RetryDelayHours: 24, MaxItemsPerRun: 25, SearchWindowStartHour: null, SearchWindowEndHour: null);

    [Fact]
    public async Task Pressing_Search_wakes_the_lane_that_listens_for_planning_wake_once_and_the_automation_planner_is_that_lane()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var gate = app.Services.GetRequiredService<IJobLaneSignal>().Register("test-planning-listener", ["planning.wake"]);
        var library = Plan("library-search-now");
        await jobs.PlanLibrarySearchesAsync([library], CancellationToken.None);
        Assert.Equal(0, gate.CurrentCount);

        Assert.True(await jobs.RequestLibrarySearchAsync(library, CancellationToken.None));

        Assert.Equal(1, gate.CurrentCount);

        var planner = Assert.Single(JobLanes.All, lane => lane.SignalTypes.Contains("planning.wake"));
        Assert.Equal("planning.automation", planner.Name);
        Assert.True(planner.PlanAutomation);
        Assert.Empty(planner.JobTypes); // a sentinel: no job of this type is ever queued
    }
}
