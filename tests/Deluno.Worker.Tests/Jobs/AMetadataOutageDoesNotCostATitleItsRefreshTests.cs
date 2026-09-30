using Deluno.Contracts;
using Deluno.Integrations.Metadata;
using Deluno.Jobs.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>
/// #1090: a refresh that ends because the metadata service did not answer is
/// not stamped as attempted (the title would wait 24 hours for an outage of a
/// minute), and a show whose episode list could not be read says so on its own
/// page instead of quietly keeping what it had.
/// </summary>
public sealed class AMetadataOutageDoesNotCostATitleItsRefreshTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-01T03:00:00Z");

    private static readonly IntegrationFailure Outage = IntegrationFailureFactory.FromLegacy(
        "metadata", "metadata:broker:x", "the metadata service", "metadata.broker.search", "connectivity", "The service returned HTTP 503.");

    private static MovieListItem Movie(string? providerId = null) => new(
        "movie-1", "Arrival", 2016, "tt2543164", true, true,
        providerId is null ? null : "tmdb", providerId, null, null, null, null, null, [], null, null, null, null, Now, Now);

    private static SeriesListItem Series(string? providerId = null) => new(
        "series-1", "Severance", 2022, "tt11280740", true, true,
        providerId is null ? null : "tmdb", providerId, null, null, null, null, null, [], null, null, null, null, Now, Now);

    private sealed class FakeScope(IntegrationFailure? failure) : IMetadataFailureScope
    {
        public IntegrationFailure? Failure => failure;
        public void Dispose() { }
    }

    private static Mock<IMetadataProvider> Provider(IntegrationFailure? failure)
    {
        var provider = new Mock<IMetadataProvider>();
        provider.Setup(p => p.BeginFailureScope()).Returns(new FakeScope(failure));
        return provider;
    }

    private static (MoviesMetadataRefreshJobHandler Handler, Mock<IMovieCatalogRepository> Repo) MovieHandler(
        Mock<IMetadataProvider> provider,
        string? providerId)
    {
        var repo = new Mock<IMovieCatalogRepository>();
        repo.Setup(r => r.GetByIdAsync("movie-1", It.IsAny<CancellationToken>())).ReturnsAsync(Movie(providerId));
        return (new MoviesMetadataRefreshJobHandler(provider.Object, repo.Object, new Mock<IActivityFeedRepository>().Object), repo);
    }

    [Fact]
    public async Task A_movie_whose_provider_is_unavailable_is_not_stamped_as_attempted()
    {
        var provider = Provider(Outage);
        provider.Setup(p => p.ResolveProviderRecordAsync(It.IsAny<MetadataLookupRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetadataProviderRecordLookup(MetadataProviderRecordStatus.Unavailable, "tmdb", "329865", Failure: Outage));
        var (handler, repo) = MovieHandler(provider, "329865");

        var message = await handler.HandleAsync(TestJobs.Create("movies.metadata.refresh", relatedEntityId: "movie-1"), CancellationToken.None);

        Assert.Contains("temporarily unavailable", message);
        repo.Verify(r => r.RecordMetadataAttemptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_movie_that_came_back_empty_because_the_service_failed_is_not_stamped_and_is_not_called_unmatched()
    {
        var provider = Provider(Outage);
        provider.Setup(p => p.SearchAsync(It.IsAny<MetadataLookupRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var (handler, repo) = MovieHandler(provider, null);

        var message = await handler.HandleAsync(TestJobs.Create("movies.metadata.refresh", relatedEntityId: "movie-1"), CancellationToken.None);

        Assert.DoesNotContain("No metadata match", message);
        Assert.Contains("did not answer", message);
        repo.Verify(r => r.RecordMetadataAttemptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_movie_the_provider_really_has_no_match_for_is_still_stamped()
    {
        var provider = Provider(null);
        provider.Setup(p => p.SearchAsync(It.IsAny<MetadataLookupRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var (handler, repo) = MovieHandler(provider, null);

        var message = await handler.HandleAsync(TestJobs.Create("movies.metadata.refresh", relatedEntityId: "movie-1"), CancellationToken.None);

        Assert.Equal("No metadata match found for Arrival.", message);
        repo.Verify(r => r.RecordMetadataAttemptAsync("movie-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_setup_problem_is_not_an_outage_and_is_still_stamped()
    {
        var notConfigured = IntegrationFailureFactory.FromLegacy("metadata", "tmdb", "TMDb", "metadata.provider.resolve", "configuration", "TMDb is not configured.");
        var provider = Provider(null);
        provider.Setup(p => p.ResolveProviderRecordAsync(It.IsAny<MetadataLookupRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetadataProviderRecordLookup(MetadataProviderRecordStatus.Unavailable, "tmdb", "329865", Failure: notConfigured));
        var (handler, repo) = MovieHandler(provider, "329865");

        await handler.HandleAsync(TestJobs.Create("movies.metadata.refresh", relatedEntityId: "movie-1"), CancellationToken.None);

        repo.Verify(r => r.RecordMetadataAttemptAsync("movie-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_show_whose_provider_is_unavailable_is_not_stamped_either()
    {
        var provider = Provider(Outage);
        provider.Setup(p => p.ResolveProviderRecordAsync(It.IsAny<MetadataLookupRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetadataProviderRecordLookup(MetadataProviderRecordStatus.Unavailable, "tmdb", "95396", Failure: Outage));
        var repo = new Mock<ISeriesCatalogRepository>();
        repo.Setup(r => r.GetByIdAsync("series-1", It.IsAny<CancellationToken>())).ReturnsAsync(Series("95396"));
        var handler = new SeriesMetadataRefreshJobHandler(provider.Object, repo.Object, new Mock<IActivityFeedRepository>().Object);

        await handler.HandleAsync(TestJobs.Create("series.metadata.refresh", relatedEntityId: "series-1"), CancellationToken.None);

        repo.Verify(r => r.RecordMetadataAttemptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static readonly MetadataSearchResult Match = new("tmdb", "95396", "tv", "Severance", null, 2022, "overview", null, null, 8.4, [], ["Drama"], "tt11280740", null);

    private static (SeriesMetadataRefreshJobHandler Handler, Mock<ISeriesCatalogRepository> Repo) ShowHandler(
        IntegrationFailure? scopeFailure,
        Action<Mock<IMetadataProvider>> catalogue)
    {
        var provider = Provider(scopeFailure);
        provider.Setup(p => p.ResolveProviderRecordAsync(It.IsAny<MetadataLookupRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetadataProviderRecordLookup(MetadataProviderRecordStatus.Found, "tmdb", "95396", Match));
        catalogue(provider);
        var repo = new Mock<ISeriesCatalogRepository>();
        repo.Setup(r => r.GetByIdAsync("series-1", It.IsAny<CancellationToken>())).ReturnsAsync(Series("95396"));
        repo.Setup(r => r.RecordMetadataProviderIssueAsync(It.IsAny<string>(), It.IsAny<MetadataProviderIssue>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        repo.Setup(r => r.SyncEpisodeCatalogueAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<CatalogueEpisodeItem>>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(SeriesCatalogueSyncResult.None);
        var activity = new Mock<IActivityFeedRepository>();
        return (new SeriesMetadataRefreshJobHandler(provider.Object, repo.Object, activity.Object), repo);
    }

    [Fact]
    public async Task A_catalogue_that_could_not_be_read_leaves_a_note_on_the_show_instead_of_vanishing()
    {
        var (handler, repo) = ShowHandler(
            Outage,
            provider => provider
                .Setup(p => p.GetSeriesCatalogueAsync("95396", It.IsAny<MetadataCatalogueSource>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HttpRequestException("down")));

        await handler.HandleAsync(TestJobs.Create("series.metadata.refresh", relatedEntityId: "series-1"), CancellationToken.None);

        repo.Verify(r => r.RecordMetadataProviderIssueAsync(
            "series-1",
            It.Is<MetadataProviderIssue>(issue => issue.Kind == SeriesMetadataRefreshJobHandler.CatalogueUnreadable && issue.AcknowledgedUtc == null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task An_empty_list_that_is_only_empty_because_the_service_failed_leaves_the_same_note()
    {
        var (handler, repo) = ShowHandler(
            Outage,
            provider => provider
                .Setup(p => p.GetSeriesCatalogueAsync("95396", It.IsAny<MetadataCatalogueSource>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]));

        await handler.HandleAsync(TestJobs.Create("series.metadata.refresh", relatedEntityId: "series-1"), CancellationToken.None);

        repo.Verify(r => r.RecordMetadataProviderIssueAsync(
            "series-1",
            It.Is<MetadataProviderIssue>(issue => issue.Kind == SeriesMetadataRefreshJobHandler.CatalogueUnreadable),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_new_show_synced_while_thetvdb_was_down_says_it_used_tmdbs_numbering()
    {
        var season = new MetadataSeason(1, "Season 1", 1, null, [new MetadataEpisode(1, 1, "Good News About Hell", null, null, null, "tmdb")]);
        var (handler, repo) = ShowHandler(
            Outage,
            provider => provider
                .Setup(p => p.GetSeriesCatalogueAsync("95396", It.IsAny<MetadataCatalogueSource>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([season]));

        await handler.HandleAsync(TestJobs.Create("series.metadata.refresh", relatedEntityId: "series-1"), CancellationToken.None);

        repo.Verify(r => r.RecordMetadataProviderIssueAsync(
            "series-1",
            It.Is<MetadataProviderIssue>(issue => issue.Kind == SeriesMetadataRefreshJobHandler.CatalogueTvdbSkipped),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_clean_read_clears_the_note_but_leaves_a_different_note_alone()
    {
        var season = new MetadataSeason(1, "Season 1", 1, null, [new MetadataEpisode(1, 1, "Good News About Hell", null, null, null, "tmdb")]);
        var (handler, repo) = ShowHandler(
            null,
            provider => provider
                .Setup(p => p.GetSeriesCatalogueAsync("95396", It.IsAny<MetadataCatalogueSource>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([season]));
        repo.Setup(r => r.GetMetadataProviderIssueAsync("series-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetadataProviderIssue(SeriesMetadataRefreshJobHandler.CatalogueUnreadable, "tmdb", "95396", "k", Now, null));

        await handler.HandleAsync(TestJobs.Create("series.metadata.refresh", relatedEntityId: "series-1"), CancellationToken.None);
        repo.Verify(r => r.ClearMetadataProviderIssueAsync("series-1", It.IsAny<CancellationToken>()), Times.Once);

        repo.Invocations.Clear();
        repo.Setup(r => r.GetMetadataProviderIssueAsync("series-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetadataProviderIssue("provider-record-missing", "tmdb", "95396", "k", Now, null));
        await handler.HandleAsync(TestJobs.Create("series.metadata.refresh", relatedEntityId: "series-1"), CancellationToken.None);
        repo.Verify(r => r.ClearMetadataProviderIssueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
