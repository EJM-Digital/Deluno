using System.Runtime.CompilerServices;
using Deluno.Worker.Jobs;
using Deluno.Worker.Services;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// Every job handler has a lane that will lease its work.
///
/// <para>DelunoHeartbeatWorker refuses to start if a registered handler's job
/// type is routed by no lane, and that stops the whole host. Without this test
/// the gap only shows up when the app is launched: #920's refresh retry shipped
/// a handler with no lane and the host never came up.</para>
/// </summary>
public sealed class EveryJobHandlerHasALaneTests
{
    [Fact]
    public void Every_job_handler_in_the_worker_is_leased_by_a_lane()
    {
        var routed = JobLanes.All
            .SelectMany(lane => lane.JobTypes)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var handlerTypes = typeof(IJobHandler).Assembly.GetTypes()
            .Where(type => typeof(IJobHandler).IsAssignableFrom(type) && type is { IsClass: true, IsAbstract: false })
            .ToArray();
        Assert.NotEmpty(handlerTypes);

        var unrouted = handlerTypes
            .Select(type => (Type: type, JobType: ((IJobHandler)RuntimeHelpers.GetUninitializedObject(type)).JobType))
            .Where(handler => !routed.Contains(handler.JobType))
            .Select(handler => $"{handler.Type.Name}: {handler.JobType}")
            .ToArray();

        Assert.Empty(unrouted);
    }
}
