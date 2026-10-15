using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.Metadata;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// POST /api/movies/{id}/metadata/link and POST /api/series/{id}/metadata/link (the "link to another record" remap):
/// a title is re-linked only after its preview, only with that preview's token, and nothing changes when the record is
/// gone, the service is down or the token is stale. The metadata service is a stand-in that answers as the real one does;
/// what is under test is what the routes and the catalogue do with the answer.
/// </summary>
public sealed class MetadataLinkRoutesTests
{
    private sealed class LinkProvider : IMetadataProvider
    {
        public MetadataProviderRecordStatus Status = MetadataProviderRecordStatus.Found;
        public string ProviderId = "9001";
        public string Title = "Dune";
        public int Year = 2021;

        private MetadataSearchResult Record(string mediaType) => new(
            "tmdb", ProviderId, mediaType, Title, null, Year, "A record the person picked.", null, null, 7.5, [], ["Drama"],
            "tt0000009", null, Network: mediaType == "tv" ? "HBO" : null);

        public Task<MetadataProviderRecordLookup> ResolveProviderRecordAsync(MetadataLookupRequest request, CancellationToken cancellationToken)
            => Task.FromResult(Status switch
            {
                MetadataProviderRecordStatus.Found => new MetadataProviderRecordLookup(Status, "tmdb", ProviderId, Record(request.MediaType ?? "movies")),
                MetadataProviderRecordStatus.Missing => new MetadataProviderRecordLookup(Status, "tmdb", ProviderId),
                _ => new MetadataProviderRecordLookup(Status, "tmdb", ProviderId, null,
                    IntegrationFailureFactory.FromLegacy("metadata", "tmdb", "the metadata service", "metadata.provider.record", "connectivity", "The service returned HTTP 503."))
            });

        public Task<IReadOnlyList<MetadataSeason>> GetSeriesCatalogueAsync(string providerId, MetadataCatalogueSource source, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MetadataSeason>>(
            [
                new MetadataSeason(1, "Season 1", 1, null, [new MetadataEpisode(1, 1, "Pilot", null, null)])
            ]);

        public Task<MetadataReleaseDates> GetMovieReleaseDatesAsync(string providerId, CancellationToken cancellationToken) => Task.FromResult(MetadataReleaseDates.None);
        public Task<MetadataProviderStatus> GetStatusAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MetadataProviderStatus> CheckReachabilityAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(MetadataLookupRequest request, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MetadataSearchResult>>([]);
        public Task<IReadOnlyList<MetadataEpisodeGroup>> GetSeriesEpisodeGroupsAsync(string providerId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MetadataEpisodeGroup>>([]);
        public Task<MetadataCollection?> GetMovieCollectionAsync(string providerId, CancellationToken cancellationToken) => Task.FromResult<MetadataCollection?>(null);
        public Task<IReadOnlyList<MetadataSearchResult>> GetPopularAsync(string mediaType, int page, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MetadataSearchResult>>([]);
    }

    private static Task<ApplicationTestHost> StartAsync(LinkProvider provider)
        => ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.RemoveAll<IMetadataProvider>();
            services.AddSingleton<IMetadataProvider>(provider);
        });

    private static async Task<string> TokenAsync(ApplicationTestHost app, string kind, string id, string providerId)
    {
        var preview = await app.Client.PostAsJsonAsync($"/api/{kind}/{id}/metadata/link/preview", new { providerId });
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var body = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("canApply").GetBoolean());
        return body.GetProperty("confirmationToken").GetString()!;
    }

    [Fact]
    public async Task A_film_is_linked_to_the_record_the_person_picked_only_with_the_token_of_its_preview_and_the_link_is_written_to_Activity()
    {
        var provider = new LinkProvider { Title = "Dune: Part One", ProviderId = "9001" };
        await using var app = await StartAsync(provider);
        var film = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021);
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();

        Assert.Equal(HttpStatusCode.BadRequest,
            (await app.Client.PostAsJsonAsync($"/api/movies/{film.MovieId}/metadata/link", new { providerId = "9001" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await app.Client.PostAsJsonAsync($"/api/movies/{film.MovieId}/metadata/link", new { providerId = "", confirmationToken = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await app.Client.PostAsJsonAsync("/api/movies/no-such-film/metadata/link", new { providerId = "9001", confirmationToken = "x" })).StatusCode);
        Assert.NotEqual("9001", (await movies.GetByIdAsync(film.MovieId, CancellationToken.None))!.MetadataProviderId);

        var token = await TokenAsync(app, "movies", film.MovieId, "9001");
        var stale = await app.Client.PostAsJsonAsync($"/api/movies/{film.MovieId}/metadata/link", new { providerId = "9001", confirmationToken = token + "-changed" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("metadata-link-preview-stale", (await stale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.NotEqual("9001", (await movies.GetByIdAsync(film.MovieId, CancellationToken.None))!.MetadataProviderId);

        var linked = await app.Client.PostAsJsonAsync($"/api/movies/{film.MovieId}/metadata/link", new { providerId = "9001", confirmationToken = token });

        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        var after = (await movies.GetByIdAsync(film.MovieId, CancellationToken.None))!;
        Assert.Equal("9001", after.MetadataProviderId);
        Assert.Equal("Dune: Part One", after.Title);
        Assert.True(File.Exists(film.FilePath), "re-linking never touches the film's file");
        Assert.Contains("metadata.movie.linked", await app.Client.GetStringAsync("/api/activity?take=50"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_film_is_left_alone_when_the_chosen_record_is_gone_or_the_metadata_service_is_down()
    {
        var provider = new LinkProvider();
        await using var app = await StartAsync(provider);
        var film = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021);
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        var before = (await movies.GetByIdAsync(film.MovieId, CancellationToken.None))!.MetadataProviderId;

        provider.Status = MetadataProviderRecordStatus.Missing;
        Assert.Equal(HttpStatusCode.NotFound,
            (await app.Client.PostAsJsonAsync($"/api/movies/{film.MovieId}/metadata/link", new { providerId = "9001", confirmationToken = "x" })).StatusCode);

        provider.Status = MetadataProviderRecordStatus.Unavailable;
        var down = await app.Client.PostAsJsonAsync($"/api/movies/{film.MovieId}/metadata/link", new { providerId = "9001", confirmationToken = "x" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
        Assert.Contains("Nothing was changed.", await down.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal(before, (await movies.GetByIdAsync(film.MovieId, CancellationToken.None))!.MetadataProviderId);
    }

    [Fact]
    public async Task A_film_is_not_linked_to_a_record_another_film_already_owns()
    {
        var provider = new LinkProvider { Title = "Arrival", Year = 2016, ProviderId = "7777" };
        await using var app = await StartAsync(provider);
        var dune = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021);
        var arrival = await ApiScenes.FilmOnDiskAsync(app, "Arrival", 2016, dune.LibraryId);
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        var token = await TokenAsync(app, "movies", arrival.MovieId, "7777");
        Assert.Equal(HttpStatusCode.OK, (await app.Client.PostAsJsonAsync($"/api/movies/{arrival.MovieId}/metadata/link", new { providerId = "7777", confirmationToken = token })).StatusCode);

        var duneAskedForArrivalsRecord = await app.Client.PostAsJsonAsync($"/api/movies/{dune.MovieId}/metadata/link/preview", new { providerId = "7777" });
        Assert.Equal(HttpStatusCode.OK, duneAskedForArrivalsRecord.StatusCode);
        var preview = await duneAskedForArrivalsRecord.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(preview.GetProperty("canApply").GetBoolean());

        var refused = await app.Client.PostAsJsonAsync($"/api/movies/{dune.MovieId}/metadata/link", new { providerId = "7777", confirmationToken = preview.GetProperty("confirmationToken").GetString() });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("metadata-link-blocked", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal("Dune", (await movies.GetByIdAsync(dune.MovieId, CancellationToken.None))!.Title);
    }

    [Fact]
    public async Task A_show_is_linked_to_the_record_the_person_picked_only_with_the_token_of_its_preview_and_keeps_its_episode_file()
    {
        var provider = new LinkProvider { Title = "The Wire", Year = 2002, ProviderId = "4242" };
        await using var app = await StartAsync(provider);
        var show = await ApiScenes.ShowOnDiskAsync(app, "The Wire", 2002);
        var series = app.Services.GetRequiredService<ISeriesCatalogRepository>();

        Assert.Equal(HttpStatusCode.BadRequest,
            (await app.Client.PostAsJsonAsync($"/api/series/{show.SeriesId}/metadata/link", new { providerId = "4242" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await app.Client.PostAsJsonAsync("/api/series/no-such-show/metadata/link", new { providerId = "4242", confirmationToken = "x" })).StatusCode);

        var token = await TokenAsync(app, "series", show.SeriesId, "4242");
        var stale = await app.Client.PostAsJsonAsync($"/api/series/{show.SeriesId}/metadata/link", new { providerId = "4242", confirmationToken = token + "-changed" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("metadata-link-preview-stale", (await stale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.NotEqual("4242", (await series.GetByIdAsync(show.SeriesId, CancellationToken.None))!.MetadataProviderId);

        var linked = await app.Client.PostAsJsonAsync($"/api/series/{show.SeriesId}/metadata/link", new { providerId = "4242", confirmationToken = token });

        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        Assert.Equal("4242", (await series.GetByIdAsync(show.SeriesId, CancellationToken.None))!.MetadataProviderId);
        Assert.True(File.Exists(show.FilePath), "re-linking never touches an episode file");
        Assert.Contains("metadata.series.linked", await app.Client.GetStringAsync("/api/activity?take=50"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_show_is_left_alone_when_the_chosen_record_is_gone_or_the_metadata_service_is_down()
    {
        var provider = new LinkProvider { Title = "The Wire", Year = 2002 };
        await using var app = await StartAsync(provider);
        var show = await ApiScenes.ShowOnDiskAsync(app, "The Wire", 2002);
        var series = app.Services.GetRequiredService<ISeriesCatalogRepository>();
        var before = (await series.GetByIdAsync(show.SeriesId, CancellationToken.None))!.MetadataProviderId;

        provider.Status = MetadataProviderRecordStatus.Missing;
        Assert.Equal(HttpStatusCode.NotFound,
            (await app.Client.PostAsJsonAsync($"/api/series/{show.SeriesId}/metadata/link", new { providerId = "4242", confirmationToken = "x" })).StatusCode);

        provider.Status = MetadataProviderRecordStatus.Unavailable;
        var down = await app.Client.PostAsJsonAsync($"/api/series/{show.SeriesId}/metadata/link", new { providerId = "4242", confirmationToken = "x" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
        Assert.Contains("nothing was changed", await down.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        Assert.Equal(before, (await series.GetByIdAsync(show.SeriesId, CancellationToken.None))!.MetadataProviderId);
    }

    [Fact]
    public async Task Previewing_a_link_says_what_would_change_and_changes_nothing_and_a_missing_title_record_or_service_is_said_plainly()
    {
        var provider = new LinkProvider { Title = "Dune: Part One", ProviderId = "9001" };
        await using var app = await StartAsync(provider);
        var film = await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021);
        var show = await ApiScenes.ShowOnDiskAsync(app, "The Wire", 2002);
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        var before = (await movies.GetByIdAsync(film.MovieId, CancellationToken.None))!.MetadataProviderId;

        var preview = await app.Client.PostAsJsonAsync($"/api/movies/{film.MovieId}/metadata/link/preview", new { providerId = "9001" });
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var body = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("canApply").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("confirmationToken").GetString()));
        Assert.Equal(before, (await movies.GetByIdAsync(film.MovieId, CancellationToken.None))!.MetadataProviderId);
        Assert.Equal(HttpStatusCode.OK, (await app.Client.PostAsJsonAsync($"/api/series/{show.SeriesId}/metadata/link/preview", new { providerId = "9001" })).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsJsonAsync("/api/movies/no-such-film/metadata/link/preview", new { providerId = "9001" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsJsonAsync("/api/series/no-such-show/metadata/link/preview", new { providerId = "9001" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PostAsJsonAsync($"/api/movies/{film.MovieId}/metadata/link/preview", new { providerId = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PostAsJsonAsync($"/api/series/{show.SeriesId}/metadata/link/preview", new { providerId = "" })).StatusCode);

        provider.Status = MetadataProviderRecordStatus.Missing;
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsJsonAsync($"/api/movies/{film.MovieId}/metadata/link/preview", new { providerId = "9001" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsJsonAsync($"/api/series/{show.SeriesId}/metadata/link/preview", new { providerId = "9001" })).StatusCode);
        provider.Status = MetadataProviderRecordStatus.Unavailable;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await app.Client.PostAsJsonAsync($"/api/movies/{film.MovieId}/metadata/link/preview", new { providerId = "9001" })).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await app.Client.PostAsJsonAsync($"/api/series/{show.SeriesId}/metadata/link/preview", new { providerId = "9001" })).StatusCode);
    }
}
