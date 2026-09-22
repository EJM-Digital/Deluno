using System.Runtime.CompilerServices;
using Deluno.Intake.Data;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Platform;
using Moq;

namespace Deluno.Worker.Tests.Retention;

/// <summary>
/// #537: a removed title's grabs are archived with it. Otherwise an alert on a
/// grab that stalled before the removal stays open with nothing left to settle
/// it - four did on the rig, for two removed proof films.
/// </summary>
public sealed class TitleRemovalGrabsTests
{
    [Fact]
    public async Task Removing_a_title_archives_its_open_grabs_and_says_how_many()
    {
        var dispatches = new Mock<IDownloadDispatchesRepository>();
        var grab = (DownloadDispatchItem)RuntimeHelpers.GetUninitializedObject(typeof(DownloadDispatchItem));
        // Archived grabs are read but not archived again: they are how a case
        // raised about a finished download is still reached (#563).
        dispatches.Setup(repository => repository.QueryDispatchesAsync(
                It.Is<DispatchQueryFilter>(filter => filter.EntityType == "movie" && filter.EntityId == "m1" && filter.IncludeArchived),
                It.IsAny<DispatchPaginationOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(([
                grab with { Id = "grab-1" },
                grab with { Id = "grab-2" },
                grab with { Id = "grab-done", Status = "archived" }
            ], (string?)null));
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repository => repository.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var removal = new TitleRemovalService(
            libraries.Object,
            Mock.Of<IIntakeRepository>(),
            Mock.Of<IJobQueueRepository>(),
            Mock.Of<IActivityFeedRepository>(),
            Mock.Of<IRecycleBinService>(),
            dispatches.Object);

        var metadata = await removal.RemoveAsync(
            new TitleToRemove("movies", "movie", "m1", "Dementia 13", 1963, null, (_, _) => AsyncEnumerable.Empty<string>(), _ => Task.FromResult(true), (_, _, _) => Task.FromResult(0)),
            new TitleRemovalOptions(DeleteFiles: false, AddImportListExclusion: false),
            CancellationToken.None);

        dispatches.Verify(repository => repository.ArchiveDispatchAsync("grab-1", "Dementia 13 was removed from Deluno.", It.IsAny<CancellationToken>()), Times.Once);
        dispatches.Verify(repository => repository.ArchiveDispatchAsync("grab-2", "Dementia 13 was removed from Deluno.", It.IsAny<CancellationToken>()), Times.Once);
        dispatches.Verify(repository => repository.ArchiveDispatchAsync("grab-done", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal("2", metadata["archivedGrabCount"]);
    }
}
