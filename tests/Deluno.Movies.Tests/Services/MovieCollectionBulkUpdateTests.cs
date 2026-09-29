using Deluno.Integrations.Metadata;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Movies.Services;
using Deluno.Quality;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;
using Moq;

namespace Deluno.Movies.Tests.Services;

/// <summary>#998: Radarr's collection editor, several collections at once.</summary>
public sealed class MovieCollectionBulkUpdateTests
{
    private static readonly DateTimeOffset Stamp = DateTimeOffset.Parse("2026-09-29T00:00:00Z");

    private static MovieCollectionItem Item(string id)
        => new(id, "tmdb", id, id, null, null, null, "lib-4k", "Movies 4K", "E:\\4K", true, true, "p1", "Ultra HD", "inCinemas", true,
            0, 0, 0, null, null, null, Stamp, Stamp);

    private static (MovieCollectionService Service, Mock<IMovieCollectionsRepository> Repo, Mock<IActivityFeedRepository> Activity) Build()
    {
        var repo = new Mock<IMovieCollectionsRepository>();
        repo.Setup(r => r.UpdateAsync(It.IsAny<string>(), It.IsAny<UpdateMovieCollectionRequest>(), It.IsAny<CancellationToken>(), It.IsAny<MovieCollectionResolvedTargets?>()))
            .ReturnsAsync((string id, UpdateMovieCollectionRequest _, CancellationToken _, MovieCollectionResolvedTargets? _) => Item(id));
        var quality = new Mock<IQualityRepository>();
        quality.Setup(q => q.ListQualityProfilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            [new QualityProfileItem("p1", "Ultra HD", "movies", "Remux-2160p", "", "", true, true, false, null, null, false, Stamp, Stamp)]);
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(l => l.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([
            new LibraryItem("lib-4k", "Movies 4K", "movies", "", "E:\\4K", null, null, null, null, true, true, "direct", null, null, 60, "hold", true, true, true, 6, 12, 50, null, null, "active", false, null, null, Stamp, Stamp)]);
        var activity = new Mock<IActivityFeedRepository>();
        var service = new MovieCollectionService(
            repo.Object, new Mock<IMetadataProvider>().Object, new Mock<IMovieCatalogRepository>().Object, libraries.Object, quality.Object,
            new Mock<IMediaDecisionService>().Object, new Mock<IJobQueueRepository>().Object, TimeProvider.System,
            null, activity.Object);
        return (service, repo, activity);
    }

    [Fact]
    public async Task Applies_profile_availability_folder_and_search_on_add_to_every_collection_and_says_so_in_Activity()
    {
        var (service, repo, activity) = Build();

        var updated = await service.UpdateManyAsync(["a", "b"], new UpdateMovieCollectionRequest(
            QualityProfileId: "p1", MinimumAvailability: "inCinemas", SearchOnAdd: true, LibraryId: "lib-4k"), CancellationToken.None);

        Assert.Equal(2, updated.Count);
        repo.Verify(r => r.UpdateAsync(It.IsAny<string>(), It.Is<UpdateMovieCollectionRequest>(x => x.LibraryId == "lib-4k" && x.SearchOnAdd == true),
            It.IsAny<CancellationToken>(), It.Is<MovieCollectionResolvedTargets?>(t => t!.QualityProfileName == "Ultra HD" && t.LibraryName == "Movies 4K" && t.RootPath == "E:\\4K")), Times.Exactly(2));
        activity.Verify(a => a.RecordActivityAsync("collection.edited", It.Is<string>(m => m.Contains("2 movie collections") && m.Contains("Ultra HD")),
            It.IsAny<string?>(), It.IsAny<string?>(), "movie-collection", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Removing_collections_deletes_only_the_collections_says_so_in_Activity_and_never_touches_a_movie()
    {
        var (service, repo, activity) = Build();
        repo.Setup(r => r.GetAsync("a", It.IsAny<CancellationToken>())).ReturnsAsync(Item("a"));
        repo.Setup(r => r.GetAsync("b", It.IsAny<CancellationToken>())).ReturnsAsync(Item("b"));
        repo.Setup(r => r.GetAsync("gone", It.IsAny<CancellationToken>())).ReturnsAsync((MovieCollectionItem?)null);
        repo.Setup(r => r.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var removed = await service.RemoveManyAsync(["a", "b", "a", "gone"], CancellationToken.None);

        Assert.Equal(["a", "b"], removed.Select(item => item.Id));
        repo.Verify(r => r.DeleteAsync("a", It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.DeleteAsync("gone", It.IsAny<CancellationToken>()), Times.Never);
        activity.Verify(a => a.RecordActivityAsync("collection.removed", It.Is<string>(m => m.Contains("2 movie collections") && m.Contains("left exactly as they are")),
            It.IsAny<string?>(), It.IsAny<string?>(), "movie-collection", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        await Assert.ThrowsAsync<ArgumentException>(() => service.RemoveManyAsync([], CancellationToken.None));
    }

    [Fact]
    public async Task Refuses_an_unknown_profile_library_or_availability_and_changes_nothing()
    {
        var (service, repo, _) = Build();

        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateManyAsync(["a"], new UpdateMovieCollectionRequest(QualityProfileId: "nope"), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateManyAsync(["a"], new UpdateMovieCollectionRequest(LibraryId: "nope"), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateManyAsync(["a"], new UpdateMovieCollectionRequest(MinimumAvailability: "someday"), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateManyAsync([], new UpdateMovieCollectionRequest(SearchOnAdd: true), CancellationToken.None));

        repo.Verify(r => r.UpdateAsync(It.IsAny<string>(), It.IsAny<UpdateMovieCollectionRequest>(), It.IsAny<CancellationToken>(), It.IsAny<MovieCollectionResolvedTargets?>()), Times.Never);
    }
}
