using Deluno.Contracts;
using Deluno.Intake.Data;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Platform;
using Moq;

namespace Deluno.Worker.Tests.Retention;

/// <summary>
/// #662: manual title/episode removal was the one place a title's files left
/// the library without telling a media server - retention already did.
/// </summary>
public sealed class TitleRemovalMediaServerNotifyTests
{
    private static LibraryItem Library() => (LibraryItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem)) is { } item
        ? item with { Id = "lib-1" }
        : throw new InvalidOperationException();

    private static async IAsyncEnumerable<string> OneFile(string path)
    {
        await Task.Yield();
        yield return path;
    }

    [Fact]
    public async Task Deleting_files_queues_a_media_server_refresh_naming_every_tracked_file_as_deleted()
    {
        var library = Library();
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repository => repository.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([library]);

        var recycleBin = new Mock<IRecycleBinService>();
        recycleBin.Setup(service => service.MoveAsync(It.IsAny<IReadOnlyList<TrackedLibraryFile>>(), It.IsAny<IReadOnlyList<LibraryItem>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecycleBinMoveResult(1, 0, [], []));

        var scheduler = new Mock<IJobScheduler>();
        EnqueueJobRequest? captured = null;
        scheduler.Setup(s => s.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()))
            .Callback<EnqueueJobRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync((JobQueueItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(JobQueueItem)));

        var removal = new TitleRemovalService(
            libraries.Object,
            Mock.Of<IIntakeRepository>(),
            Mock.Of<IJobQueueRepository>(),
            Mock.Of<IActivityFeedRepository>(),
            recycleBin.Object,
            mediaServerJobScheduler: scheduler.Object);

        await removal.RemoveAsync(
            new TitleToRemove(
                "movies", "movie", "m1", "Dementia 13", 1963, null,
                (_, _) => OneFile("/media/Movies/Dementia 13 (1963)/Dementia 13.mkv"),
                _ => Task.FromResult(true),
                (_, _, _) => Task.FromResult(0)),
            new TitleRemovalOptions(DeleteFiles: true, AddImportListExclusion: false),
            CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(MediaServerJobTypes.Refresh, captured!.JobType);
        var payload = System.Text.Json.JsonSerializer.Deserialize<MediaServerRefreshJobPayload>(captured.PayloadJson!)!;
        Assert.Equal("movies", payload.MediaType);
        var change = Assert.Single(payload.Changes);
        Assert.Equal("/media/Movies/Dementia 13 (1963)/Dementia 13.mkv", change.Path);
        Assert.Equal(MediaServerChangeKind.Deleted, change.Kind);
    }

    [Fact]
    public async Task Removing_a_title_without_deleting_files_never_queues_a_refresh()
    {
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repository => repository.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var scheduler = new Mock<IJobScheduler>();

        var removal = new TitleRemovalService(
            libraries.Object,
            Mock.Of<IIntakeRepository>(),
            Mock.Of<IJobQueueRepository>(),
            Mock.Of<IActivityFeedRepository>(),
            Mock.Of<IRecycleBinService>(),
            mediaServerJobScheduler: scheduler.Object);

        await removal.RemoveAsync(
            new TitleToRemove("movies", "movie", "m1", "Dementia 13", 1963, null, (_, _) => AsyncEnumerable.Empty<string>(), _ => Task.FromResult(true), (_, _, _) => Task.FromResult(0)),
            new TitleRemovalOptions(DeleteFiles: false, AddImportListExclusion: false),
            CancellationToken.None);

        scheduler.Verify(s => s.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
