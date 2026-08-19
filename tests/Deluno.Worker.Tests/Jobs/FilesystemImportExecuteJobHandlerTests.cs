using Deluno.Filesystem;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

public sealed class FilesystemImportExecuteJobHandlerTests
{
    private const string ValidPayload =
        """
        {"preview":{"sourcePath":"C:\\downloads\\movie.mkv"},"transferMode":"auto","overwrite":false,"allowCopyFallback":true}
        """;

    [Fact]
    public async Task HandleAsync_malformed_payload_throws_instead_of_silently_succeeding()
    {
        var importPipelineService = new Mock<IImportPipelineService>(MockBehavior.Strict);
        var handler = new FilesystemImportExecuteJobHandler(importPipelineService.Object);

        var job = TestJobs.Create("filesystem.import.execute", payloadJson: "not json");

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(job, CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_failed_import_throws_with_the_pipeline_message()
    {
        var importPipelineService = new Mock<IImportPipelineService>();
        importPipelineService
            .Setup(service => service.ExecuteAsync(It.IsAny<ImportExecuteRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImportPipelineResult(false, 400, null, "Destination already exists."));
        var handler = new FilesystemImportExecuteJobHandler(importPipelineService.Object);

        var job = TestJobs.Create("filesystem.import.execute", payloadJson: ValidPayload);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(job, CancellationToken.None));
        Assert.Equal("Destination already exists.", exception.Message);
    }

    [Fact]
    public async Task HandleAsync_successful_import_with_no_response_returns_the_default_completion_message()
    {
        var importPipelineService = new Mock<IImportPipelineService>();
        importPipelineService
            .Setup(service => service.ExecuteAsync(It.IsAny<ImportExecuteRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImportPipelineResult(true, 200, null, ""));
        var handler = new FilesystemImportExecuteJobHandler(importPipelineService.Object);

        var job = TestJobs.Create("filesystem.import.execute", payloadJson: ValidPayload);

        var message = await handler.HandleAsync(job, CancellationToken.None);

        Assert.Equal("Import completed.", message);
    }
}
