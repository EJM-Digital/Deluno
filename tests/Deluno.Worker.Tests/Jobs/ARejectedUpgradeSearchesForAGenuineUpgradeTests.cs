using System.Runtime.CompilerServices;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Worker.Jobs;
using Deluno.Worker.Services;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>
/// #928: an upgrade rejected at import (worse than the library file already
/// has - <c>ImportFailurePolicy.ReplacementRejected</c>, a 409) is refused the
/// same way a 400 refusal is (#603/#604): the job ends cleanly on the first
/// rejection rather than being thrown and retried against a release already
/// blocked, and Deluno searches for a genuine upgrade straight away, the
/// rejected release excluded via the blocklist.
///
/// <para>Before this fix, <c>ProcessorOutcomeReporter.IsRefusal</c> only
/// recognised status 400, so a 409 replacement rejection fell through to
/// <c>throw new InvalidOperationException(...)</c> and was retried a few
/// times against bytes that would never import.</para>
/// </summary>
public sealed class ARejectedUpgradeSearchesForAGenuineUpgradeTests
{
    private const string Release = "Arrival.2016.720p.WEB-DL-GROUP";
    private const string Payload =
        """
        {"preview":{"sourcePath":"C:\\Refined\\Movies\\Arrival.mkv"},"transferMode":"auto","overwrite":false,"allowCopyFallback":true,"dispatchId":"dispatch-1"}
        """;

    private readonly List<EnqueueJobRequest> _queued = [];

    [Fact]
    public async Task A_rejected_upgrade_is_not_thrown_or_retried()
    {
        var exception = await Record.ExceptionAsync(() => RejectAsync());
        Assert.Null(exception);
    }

    [Fact]
    public async Task A_rejected_upgrade_searches_for_a_genuine_upgrade_because_the_release_is_already_blocked()
    {
        var message = await RejectAsync();

        var search = Assert.Single(_queued);
        Assert.Equal(LibrarySearchJobTypes.For("movies"), search.JobType);
        using var payload = JsonDocument.Parse(search.PayloadJson!);
        Assert.Equal("movie-arrival", payload.RootElement.GetProperty("targetEntityId").GetString());
        Assert.Equal("import-refused", payload.RootElement.GetProperty("triggeredBy").GetString());
        Assert.EndsWith("Deluno is searching for another copy.", message);
    }

    [Fact]
    public void IsRefusal_recognises_a_replacement_rejection_as_final_not_just_status_400()
    {
        var result = new ImportPipelineResult(false, 409, null, "worse than the library file", ImportFailurePolicy.ReplacementRejected);
        Assert.True(ProcessorOutcomeReporter.IsRefusal(result));
    }

    private async Task<string> RejectAsync()
    {
        var pipeline = new Mock<IImportPipelineService>();
        pipeline
            .Setup(service => service.ExecuteAsync(It.IsAny<ImportExecuteRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImportPipelineResult(
                false,
                409,
                null,
                "The incoming file is lower resolution than the library file already has.",
                ImportFailurePolicy.ReplacementRejected));
        var dispatch = (DownloadDispatchItem)RuntimeHelpers.GetUninitializedObject(typeof(DownloadDispatchItem)) with
        {
            Id = "dispatch-1",
            LibraryId = "library-movies",
            MediaType = "movies",
            EntityType = "movie",
            EntityId = "movie-arrival",
            ReleaseName = Release
        };
        var dispatches = new Mock<IDownloadDispatchesRepository>();
        dispatches.Setup(repository => repository.GetDispatchAsync("dispatch-1", It.IsAny<CancellationToken>())).ReturnsAsync(dispatch);
        var blocklist = new Mock<IBlockedReleaseRepository>();
        blocklist
            .Setup(repository => repository.ListForAsync("movies", "movie-arrival", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new BlockedRelease(
                "b1", "key", Release, "Soak Torznab", "movies", "movie-arrival", "Arrival",
                ImportFailurePolicy.ReplacementRejected, "worse than the library file", null, null, null, DateTimeOffset.UnixEpoch)]);
        var scheduler = new Mock<IJobScheduler>();
        scheduler
            .Setup(instance => instance.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()))
            .Callback<EnqueueJobRequest, CancellationToken>((request, _) => _queued.Add(request))
            .ReturnsAsync((JobQueueItem)RuntimeHelpers.GetUninitializedObject(typeof(JobQueueItem)) with { Id = "search-1" });

        var handler = new FilesystemImportExecuteJobHandler(
            pipeline.Object,
            dispatches: dispatches.Object,
            blockedReleases: blocklist.Object,
            jobScheduler: scheduler.Object);

        return await handler.HandleAsync(TestJobs.Create("filesystem.import.execute", payloadJson: Payload), CancellationToken.None);
    }
}
