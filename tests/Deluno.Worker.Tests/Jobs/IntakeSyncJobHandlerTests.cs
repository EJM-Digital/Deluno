using Deluno.Worker.Intake;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

public sealed class IntakeSyncJobHandlerTests
{
    [Fact]
    public async Task HandleAsync_missing_source_id_skips_without_calling_the_service()
    {
        var intakeSyncService = new Mock<IIntakeSyncService>(MockBehavior.Strict);
        var handler = new IntakeSyncJobHandler(intakeSyncService.Object);

        var message = await handler.HandleAsync(TestJobs.Create("intake.sync", payloadJson: "{}"), CancellationToken.None);

        Assert.Equal("Skipped intake sync because no source id was provided.", message);
    }

    [Fact]
    public async Task HandleAsync_well_formed_payload_runs_the_sync_and_reports_the_summary()
    {
        var intakeSyncService = new Mock<IIntakeSyncService>();
        intakeSyncService
            .Setup(service => service.RunAsync("source-1", It.IsAny<string?>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntakeSyncRunResult("source-1", "Radarr List", "completed", 10, 3, 2, 1, 0, false, "added 3"));
        var handler = new IntakeSyncJobHandler(intakeSyncService.Object);

        var job = TestJobs.Create("intake.sync", payloadJson: """{"sourceId":"source-1","manual":false}""");
        var message = await handler.HandleAsync(job, CancellationToken.None);

        Assert.Equal("Intake sync completed for Radarr List: added 3", message);
    }

    [Fact]
    public async Task HandleAsync_falls_back_to_the_job_related_entity_id_when_the_payload_has_no_source_id()
    {
        var intakeSyncService = new Mock<IIntakeSyncService>();
        intakeSyncService
            .Setup(service => service.RunAsync("source-2", It.IsAny<string?>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntakeSyncRunResult("source-2", "Sonarr List", "completed", 5, 1, 0, 0, 0, false, "added 1"));
        var handler = new IntakeSyncJobHandler(intakeSyncService.Object);

        var job = TestJobs.Create("intake.sync", payloadJson: null, relatedEntityId: "source-2");
        var message = await handler.HandleAsync(job, CancellationToken.None);

        Assert.Equal("Intake sync completed for Sonarr List: added 1", message);
    }
}
