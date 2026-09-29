using Deluno.Integrations.Metadata;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Series.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Series.Tests.Services;

/// <summary>
/// #863: the TMDb-episode-groups half of episode ordering - asking TMDb for
/// every TMDb-backed title Deluno holds, and leaving everything else alone.
/// </summary>
public sealed class EpisodeGroupRefreshServiceTests
{
    [Fact]
    public async Task Only_tmdb_backed_titles_are_asked_and_dvd_and_absolute_orderings_are_applied()
    {
        var tmdbShow = Series("show-1", "TMDb Show", "tmdb", "1");
        var otherProviderShow = Series("show-2", "Other Provider Show", "tvdb", "2");

        var repository = new Mock<ISeriesCatalogRepository>();
        repository.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([tmdbShow, otherProviderShow]);
        repository.Setup(repo => repo.ApplyEpisodeGroupsAsync(
                "show-1",
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyList<SeriesEpisodeGroupMapping>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);

        var provider = new Mock<IMetadataProvider>();
        provider.Setup(p => p.GetSeriesEpisodeGroupsAsync("1", It.IsAny<MetadataCatalogueSource>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new MetadataEpisodeGroup(TmdbEpisodeGroupTypes.Dvd, "group-dvd", "DVD Order",
                [
                    new MetadataEpisodeGroupEpisode(1, 1, 1, 1),
                    new MetadataEpisodeGroupEpisode(1, 2, 1, 2)
                ]),
                new MetadataEpisodeGroup(TmdbEpisodeGroupTypes.StoryArc, "group-arc", "Story Arc", [])
            ]);

        var service = new EpisodeGroupRefreshService(repository.Object, provider.Object, NullLogger<EpisodeGroupRefreshService>.Instance);

        var summary = await service.RefreshAllAsync(CancellationToken.None);

        Assert.Equal(1, summary.SeriesChecked);
        Assert.Equal(1, summary.SeriesWithoutProviderId);
        Assert.Equal(1, summary.SeriesWithUpdatedOrderings);
        Assert.Equal(2, summary.EpisodesUpdated);

        // The non-TMDb title is never asked at all.
        provider.Verify(p => p.GetSeriesEpisodeGroupsAsync("2", It.IsAny<MetadataCatalogueSource>(), It.IsAny<CancellationToken>()), Times.Never);
        provider.Verify(p => p.GetSeriesEpisodeGroupsAsync(It.IsAny<string>(), It.IsAny<MetadataCatalogueSource>(), It.IsAny<CancellationToken>()), Times.Once);

        repository.Verify(repo => repo.ApplyEpisodeGroupsAsync(
            "show-1",
            It.Is<IReadOnlyList<string>>(schemes => schemes.Contains(SeriesNumberingSchemes.Dvd)),
            It.Is<IReadOnlyList<string>>(names => names.Contains("Story Arc")),
            It.Is<IReadOnlyList<SeriesEpisodeGroupMapping>>(mappings =>
                mappings.Count == 2 &&
                mappings.Any(m => m.SeasonNumber == 1 && m.EpisodeNumber == 1 && m.DvdSeasonNumber == 1 && m.DvdEpisodeNumber == 1) &&
                mappings.Any(m => m.SeasonNumber == 1 && m.EpisodeNumber == 2 && m.DvdSeasonNumber == 1 && m.DvdEpisodeNumber == 2)),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task A_title_with_no_episode_groups_is_left_untouched()
    {
        var show = Series("show-1", "No Groups Show", "tmdb", "1");

        var repository = new Mock<ISeriesCatalogRepository>();
        repository.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([show]);

        var provider = new Mock<IMetadataProvider>();
        provider.Setup(p => p.GetSeriesEpisodeGroupsAsync("1", It.IsAny<MetadataCatalogueSource>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<MetadataEpisodeGroup>());

        var service = new EpisodeGroupRefreshService(repository.Object, provider.Object, NullLogger<EpisodeGroupRefreshService>.Instance);

        var summary = await service.RefreshAllAsync(CancellationToken.None);

        Assert.Equal(1, summary.SeriesChecked);
        Assert.Equal(0, summary.SeriesWithUpdatedOrderings);
        Assert.Equal(0, summary.EpisodesUpdated);
        repository.Verify(
            repo => repo.ApplyEpisodeGroupsAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyList<SeriesEpisodeGroupMapping>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task One_titles_lookup_failure_does_not_stop_the_rest_of_the_pass()
    {
        var failing = Series("show-1", "Unreachable Source", "tmdb", "1");
        var healthy = Series("show-2", "Reachable Source", "tmdb", "2");

        var repository = new Mock<ISeriesCatalogRepository>();
        repository.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([failing, healthy]);
        repository.Setup(repo => repo.ApplyEpisodeGroupsAsync(
                "show-2",
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyList<SeriesEpisodeGroupMapping>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var provider = new Mock<IMetadataProvider>();
        provider.Setup(p => p.GetSeriesEpisodeGroupsAsync("1", It.IsAny<MetadataCatalogueSource>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("unreachable"));
        provider.Setup(p => p.GetSeriesEpisodeGroupsAsync("2", It.IsAny<MetadataCatalogueSource>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MetadataEpisodeGroup(TmdbEpisodeGroupTypes.Absolute, "group-abs", "Absolute", [new MetadataEpisodeGroupEpisode(1, 1, 1, 1)])]);

        var service = new EpisodeGroupRefreshService(repository.Object, provider.Object, NullLogger<EpisodeGroupRefreshService>.Instance);

        var summary = await service.RefreshAllAsync(CancellationToken.None);

        Assert.Equal(2, summary.SeriesChecked);
        Assert.Equal(1, summary.SeriesWithUpdatedOrderings);
        Assert.Equal(1, summary.EpisodesUpdated);
    }

    private static SeriesListItem Series(string id, string title, string metadataProvider, string metadataProviderId)
        => new(
            Id: id,
            Title: title,
            StartYear: 2020,
            ImdbId: null,
            Monitored: true,
            HasFile: false,
            MetadataProvider: metadataProvider,
            MetadataProviderId: metadataProviderId,
            OriginalTitle: null,
            Overview: null,
            PosterUrl: null,
            BackdropUrl: null,
            Rating: null,
            Ratings: [],
            Genres: null,
            ExternalUrl: null,
            MetadataJson: null,
            MetadataUpdatedUtc: null,
            CreatedUtc: DateTimeOffset.UtcNow,
            UpdatedUtc: DateTimeOffset.UtcNow,
            SeriesType: SeriesTypes.Standard,
            NumberingScheme: SeriesNumberingSchemes.Standard,
            NumberingSource: SeriesNumberingSources.Provider);
}
