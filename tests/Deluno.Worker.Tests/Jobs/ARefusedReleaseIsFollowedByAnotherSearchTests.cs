using System.Runtime.CompilerServices;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>
/// #604: a release refused at import and blocklisted is followed straight away
/// by a search for another copy of the title.
///
/// <para><b>Why this file exists.</b> On the soak rig Breaking Bad S03E03's
/// sample was refused at 16:32. Its real copy, on the other indexer since the
/// same minute, was not fetched two hours later: nothing searched again until
/// the episode's turn in the scheduled pass.</para>
/// </summary>
public sealed class ARefusedReleaseIsFollowedByAnotherSearchTests
{
    private const string Release = "Breaking.Bad.S03E03.1080p.WEB-DL.DDP5.1.H.264-CLIPPED";
    private const string Payload =
        """
        {"preview":{"sourcePath":"C:\\Refined\\TV\\sample.mkv"},"transferMode":"auto","overwrite":false,"allowCopyFallback":true,"dispatchId":"dispatch-1"}
        """;

    private readonly List<EnqueueJobRequest> _queued = [];

    [Fact]
    public async Task A_refusal_that_blocklisted_the_release_searches_for_another_copy_of_the_episode()
    {
        var message = await RefuseAsync(blocked: true);

        var search = Assert.Single(_queued);
        Assert.Equal(LibrarySearchJobTypes.For("tv"), search.JobType);
        using var payload = JsonDocument.Parse(search.PayloadJson!);
        Assert.Equal("episode-303", payload.RootElement.GetProperty("targetEntityId").GetString());
        Assert.Equal("import-refused", payload.RootElement.GetProperty("triggeredBy").GetString());
        Assert.EndsWith("Deluno is searching for another copy.", message);
    }

    [Fact]
    public async Task A_refusal_that_blocked_nothing_searches_for_nothing()
    {
        var message = await RefuseAsync(blocked: false);

        Assert.Empty(_queued);
        Assert.Equal("Not imported: The destination file already exists.", message);
    }

    private async Task<string> RefuseAsync(bool blocked)
    {
        var pipeline = new Mock<IImportPipelineService>();
        pipeline
            .Setup(service => service.ExecuteAsync(It.IsAny<ImportExecuteRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImportPipelineResult(false, 400, null, blocked
                ? "The detected runtime is under two minutes, so Deluno is treating this as a likely sample."
                : "The destination file already exists."));
        var dispatch = (DownloadDispatchItem)RuntimeHelpers.GetUninitializedObject(typeof(DownloadDispatchItem)) with
        {
            Id = "dispatch-1",
            LibraryId = "library-tv",
            MediaType = "tv",
            EntityType = "episode",
            EntityId = "episode-303",
            ReleaseName = Release
        };
        var dispatches = new Mock<IDownloadDispatchesRepository>();
        dispatches.Setup(repository => repository.GetDispatchAsync("dispatch-1", It.IsAny<CancellationToken>())).ReturnsAsync(dispatch);
        var blocklist = new Mock<IBlockedReleaseRepository>();
        blocklist
            .Setup(repository => repository.ListForAsync("tv", "episode-303", It.IsAny<CancellationToken>()))
            .ReturnsAsync(blocked
                ? [new BlockedRelease("b1", "key", Release, "Soak Torznab", "tv", "episode-303", "Breaking Bad", "likelySample", "sample", null, null, null, DateTimeOffset.UnixEpoch)]
                : []);
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
