using Deluno.Intake.Contracts;
using Deluno.Intake.Data;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Platform;
using Moq;

namespace Deluno.Worker.Tests.Retention;

/// <summary>
/// Removing a title with "do not add it again": every import list that added it gets an exclusion, once; a list that
/// cannot be told is named in a warning and never stops the removal (coverage register, area engines).
/// </summary>
public sealed class TitleRemovalImportListExclusionTests
{
    private static IntakeTitleOriginItem Origin(string id, string sourceId, string sourceName) => new(
        id, sourceId, sourceName, "trakt", "movies", "m1", "key-" + id, "Dementia 13", 1963, "tt0056976",
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static async Task<(IDictionary<string, string?> Metadata, bool RowDeleted)> RemoveAsync(Mock<IIntakeRepository> intake, bool addExclusion)
    {
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repository => repository.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var removal = new TitleRemovalService(
            libraries.Object,
            intake.Object,
            Mock.Of<IJobQueueRepository>(),
            Mock.Of<IActivityFeedRepository>(),
            Mock.Of<IRecycleBinService>());
        var deleted = false;

        var metadata = await removal.RemoveAsync(
            new TitleToRemove("movies", "movie", "m1", "Dementia 13", 1963, "tt0056976", (_, _) => AsyncEnumerable.Empty<string>(), _ => { deleted = true; return Task.FromResult(true); }, (_, _, _) => Task.FromResult(0)),
            new TitleRemovalOptions(DeleteFiles: false, AddImportListExclusion: addExclusion),
            CancellationToken.None);
        return (metadata, deleted);
    }

    private static Mock<IIntakeRepository> IntakeWith(params IntakeTitleOriginItem[] origins)
    {
        var intake = new Mock<IIntakeRepository>();
        intake.Setup(repository => repository.ListIntakeTitleOriginsAsync("movies", "m1", It.IsAny<CancellationToken>())).ReturnsAsync(origins);
        intake.Setup(repository => repository.CreateIntakeListExclusionAsync(It.IsAny<string>(), It.IsAny<CreateIntakeListExclusionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string sourceId, CreateIntakeListExclusionRequest request, CancellationToken _) => new IntakeListExclusionItem(
                "exclusion-" + sourceId, sourceId, "key", request.Title, request.Year, request.ImdbId, request.Reason ?? "", null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
        return intake;
    }

    [Fact]
    public async Task Every_list_that_added_the_title_gets_one_exclusion_and_a_list_asked_twice_gets_only_one()
    {
        var intake = IntakeWith(Origin("o1", "list-a", "Watchlist"), Origin("o2", "list-b", "Popular"), Origin("o3", "list-a", "Watchlist"));

        var (metadata, deleted) = await RemoveAsync(intake, addExclusion: true);

        Assert.True(deleted);
        Assert.Equal("2", metadata["importListExclusionsAdded"]);
        intake.Verify(repository => repository.CreateIntakeListExclusionAsync("list-a", It.Is<CreateIntakeListExclusionRequest>(request => request.Title == "Dementia 13" && request.Year == 1963), It.IsAny<CancellationToken>()), Times.Once);
        intake.Verify(repository => repository.CreateIntakeListExclusionAsync("list-b", It.IsAny<CreateIntakeListExclusionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_list_that_cannot_take_the_exclusion_is_named_in_a_warning_and_the_removal_still_happens()
    {
        var intake = IntakeWith(Origin("o1", "list-a", "Watchlist"), Origin("o2", "list-b", "Popular"));
        intake.Setup(repository => repository.CreateIntakeListExclusionAsync("list-a", It.IsAny<CreateIntakeListExclusionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the list is locked"));

        var (metadata, deleted) = await RemoveAsync(intake, addExclusion: true);

        Assert.True(deleted);
        Assert.Equal("1", metadata["importListExclusionsAdded"]);
        Assert.Contains("Watchlist", metadata["importListExclusionWarnings"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_the_choice_no_list_is_asked_at_all()
    {
        var intake = IntakeWith(Origin("o1", "list-a", "Watchlist"));

        var (metadata, deleted) = await RemoveAsync(intake, addExclusion: false);

        Assert.True(deleted);
        Assert.False(metadata.ContainsKey("importListExclusionsAdded"));
        intake.Verify(repository => repository.CreateIntakeListExclusionAsync(It.IsAny<string>(), It.IsAny<CreateIntakeListExclusionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
