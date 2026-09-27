using Deluno.Integrations.Metadata.SceneMapping;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Series.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Series.Tests.Services;

/// <summary>
/// #863: the automatic half of anime scene numbering — asking TheXEM for
/// every anime title Deluno holds, and leaving everything else alone.
/// </summary>
public sealed class SceneMappingRefreshServiceTests
{
    [Fact]
    public async Task Only_anime_titles_with_a_tvdb_id_are_asked_and_corrections_are_applied()
    {
        var animeWithTvdb = Series("anime-1", "Anime With Mapping", SeriesTypes.Anime, """{"TvDbId":"12345"}""");
        var animeWithoutTvdb = Series("anime-2", "Anime Without TVDB", SeriesTypes.Anime, null);
        var standardShow = Series("standard-1", "A Standard Show", SeriesTypes.Standard, """{"TvDbId":"99999"}""");

        var repository = new Mock<ISeriesCatalogRepository>();
        repository.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([animeWithTvdb, animeWithoutTvdb, standardShow]);
        repository.Setup(repo => repo.ApplyAutomaticNumberingAsync(
                "anime-1",
                It.IsAny<IReadOnlyList<SeriesEpisodeSceneMapping>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);

        var provider = new Mock<ISceneMappingProvider>();
        provider.Setup(p => p.GetMappingAsync("12345", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new SceneMappingEntry(1, 5, SceneSeasonNumber: 1, SceneEpisodeNumber: 1, AbsoluteNumber: 5)
            ]);

        var service = new SceneMappingRefreshService(repository.Object, provider.Object, NullLogger<SceneMappingRefreshService>.Instance);

        var summary = await service.RefreshAllAsync(CancellationToken.None);

        Assert.Equal(2, summary.AnimeSeriesChecked);
        Assert.Equal(1, summary.SeriesWithoutTvDbId);
        Assert.Equal(1, summary.SeriesWithCorrections);
        Assert.Equal(2, summary.EpisodesUpdated);

        // The standard show is never asked at all - this is an anime-only pass.
        provider.Verify(p => p.GetMappingAsync("99999", It.IsAny<CancellationToken>()), Times.Never);
        provider.Verify(p => p.GetMappingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(
            repo => repo.ApplyAutomaticNumberingAsync("anime-2", It.IsAny<IReadOnlyList<SeriesEpisodeSceneMapping>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task A_title_with_no_recorded_mapping_is_left_untouched()
    {
        var anime = Series("anime-1", "No Mismatch Anime", SeriesTypes.Anime, """{"TvDbId":"555"}""");

        var repository = new Mock<ISeriesCatalogRepository>();
        repository.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([anime]);

        var provider = new Mock<ISceneMappingProvider>();
        provider.Setup(p => p.GetMappingAsync("555", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SceneMappingEntry>());

        var service = new SceneMappingRefreshService(repository.Object, provider.Object, NullLogger<SceneMappingRefreshService>.Instance);

        var summary = await service.RefreshAllAsync(CancellationToken.None);

        Assert.Equal(1, summary.AnimeSeriesChecked);
        Assert.Equal(0, summary.SeriesWithCorrections);
        Assert.Equal(0, summary.EpisodesUpdated);
        repository.Verify(
            repo => repo.ApplyAutomaticNumberingAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<SeriesEpisodeSceneMapping>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task One_titles_lookup_failure_does_not_stop_the_rest_of_the_pass()
    {
        var failing = Series("anime-1", "Unreachable Source", SeriesTypes.Anime, """{"TvDbId":"1"}""");
        var healthy = Series("anime-2", "Reachable Source", SeriesTypes.Anime, """{"TvDbId":"2"}""");

        var repository = new Mock<ISeriesCatalogRepository>();
        repository.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([failing, healthy]);
        repository.Setup(repo => repo.ApplyAutomaticNumberingAsync(
                "anime-2",
                It.IsAny<IReadOnlyList<SeriesEpisodeSceneMapping>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var provider = new Mock<ISceneMappingProvider>();
        provider.Setup(p => p.GetMappingAsync("1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("unreachable"));
        provider.Setup(p => p.GetMappingAsync("2", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SceneMappingEntry(1, 1, 1, 2, null)]);

        var service = new SceneMappingRefreshService(repository.Object, provider.Object, NullLogger<SceneMappingRefreshService>.Instance);

        var summary = await service.RefreshAllAsync(CancellationToken.None);

        Assert.Equal(2, summary.AnimeSeriesChecked);
        Assert.Equal(1, summary.SeriesWithCorrections);
        Assert.Equal(1, summary.EpisodesUpdated);
    }

    private static SeriesListItem Series(string id, string title, string seriesType, string? metadataJson)
        => new(
            Id: id,
            Title: title,
            StartYear: 2020,
            ImdbId: null,
            Monitored: true,
            HasFile: false,
            MetadataProvider: "tmdb",
            MetadataProviderId: id,
            OriginalTitle: null,
            Overview: null,
            PosterUrl: null,
            BackdropUrl: null,
            Rating: null,
            Ratings: [],
            Genres: null,
            ExternalUrl: null,
            MetadataJson: metadataJson,
            MetadataUpdatedUtc: null,
            CreatedUtc: DateTimeOffset.UtcNow,
            UpdatedUtc: DateTimeOffset.UtcNow,
            SeriesType: seriesType,
            NumberingScheme: SeriesNumberingSchemes.ForSeriesType(seriesType),
            NumberingSource: SeriesNumberingSources.Provider);
}
