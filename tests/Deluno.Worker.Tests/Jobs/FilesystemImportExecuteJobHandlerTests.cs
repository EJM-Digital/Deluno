using Deluno.Filesystem;
using Deluno.Jobs.Contracts;
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

    /// <summary>
    /// #603: a refusal is final, so the job ends with it rather than failing
    /// twice more and being counted as a failed job beside its recovery case.
    /// </summary>
    [Fact]
    public async Task HandleAsync_refused_import_ends_the_job_with_the_pipeline_message()
    {
        var importPipelineService = new Mock<IImportPipelineService>();
        importPipelineService
            .Setup(service => service.ExecuteAsync(It.IsAny<ImportExecuteRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImportPipelineResult(false, 400, null, "The detected runtime is under two minutes, so Deluno is treating this as a likely sample."));
        var handler = new FilesystemImportExecuteJobHandler(importPipelineService.Object);

        var job = TestJobs.Create("filesystem.import.execute", payloadJson: ValidPayload);

        var message = await handler.HandleAsync(job, CancellationToken.None);
        Assert.Equal("Not imported: The detected runtime is under two minutes, so Deluno is treating this as a likely sample.", message);
    }

    /// <summary>A failure that can clear by itself - a lock, a file still arriving - is retried.</summary>
    [Fact]
    public async Task HandleAsync_failure_that_can_clear_throws_so_it_is_retried()
    {
        var importPipelineService = new Mock<IImportPipelineService>();
        importPipelineService
            .Setup(service => service.ExecuteAsync(It.IsAny<ImportExecuteRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImportPipelineResult(false, 503, null, "The file is being used by another process."));
        var handler = new FilesystemImportExecuteJobHandler(importPipelineService.Object);

        var job = TestJobs.Create("filesystem.import.execute", payloadJson: ValidPayload);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(job, CancellationToken.None));
        Assert.Equal("The file is being used by another process.", exception.Message);
    }

    /// <summary>
    /// #652: a source file that has not settled yet - one file or a whole
    /// season pack sharing its settle window - is deferred and retried
    /// without spending one of the job's dead-letter attempts, rather than
    /// counted as a failure like every other retryable error.
    /// </summary>
    [Fact]
    public async Task HandleAsync_unstable_source_is_deferred_without_burning_an_attempt()
    {
        var importPipelineService = new Mock<IImportPipelineService>();
        importPipelineService
            .Setup(service => service.ExecuteAsync(It.IsAny<ImportExecuteRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImportPipelineResult(
                false,
                ImportFileReadiness.RetryableStatusCode,
                null,
                "The source file 'Locked.Pack.S01E02.mkv' was still being written, or locked by another process, 20 seconds after Deluno started importing this season pack. Deluno will retry when it is stable."));
        var handler = new FilesystemImportExecuteJobHandler(importPipelineService.Object);

        var job = TestJobs.Create("filesystem.import.execute", payloadJson: ValidPayload);

        var deferred = await Assert.ThrowsAsync<JobDeferredException>(() => handler.HandleAsync(job, CancellationToken.None));
        Assert.Equal(
            "The source file 'Locked.Pack.S01E02.mkv' was still being written, or locked by another process, 20 seconds after Deluno started importing this season pack. Deluno will retry when it is stable.",
            deferred.Message);
        Assert.True(deferred.Delay > TimeSpan.Zero);
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
