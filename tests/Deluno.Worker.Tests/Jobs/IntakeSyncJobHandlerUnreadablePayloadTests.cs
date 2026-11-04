using Deluno.Worker.Intake;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>An intake sync whose payload cannot be read still runs for the source the job is about, never as a hand-started run.</summary>
public sealed class IntakeSyncJobHandlerUnreadablePayloadTests
{
    [Fact]
    public async Task An_unreadable_payload_runs_the_job_s_own_source_as_a_scheduled_run()
    {
        var service = new Mock<IIntakeSyncService>();
        var job = TestJobs.Create("intake.sync", payloadJson: "not json", relatedEntityId: "source-9");
        service
            .Setup(s => s.RunAsync("source-9", job.Id, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntakeSyncRunResult("source-9", "Plex Watchlist", "completed", 0, 0, 0, 0, 0, false, "nothing new"));
        var handler = new IntakeSyncJobHandler(service.Object);

        var message = await handler.HandleAsync(job, CancellationToken.None);

        Assert.Equal("Intake sync completed for Plex Watchlist: nothing new", message);
        Assert.Equal("intake.sync", handler.JobType);
        service.VerifyAll();
    }
}
