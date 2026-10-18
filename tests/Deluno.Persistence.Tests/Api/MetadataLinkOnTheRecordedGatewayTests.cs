using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Integrations.Metadata;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1285: "Link to another record" on a film, through the real metadata provider in broker mode reading the project's metadata
/// gateway as it really answered (tests/fixtures/recorded/tmdb: the exact-record lookup of Sintel, and the lookup of a record the
/// gateway says is missing). Nothing about the metadata service is written in the test: the route, the provider's parsing of the
/// recorded reply and the catalogue are all real.
/// </summary>
public sealed class MetadataLinkOnTheRecordedGatewayTests
{
    private sealed class GatewayReplay : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            var query = request.RequestUri!.Query;
            if (request.RequestUri.AbsolutePath == "/metadata/search" && query.Contains("providerId=45745", StringComparison.Ordinal))
                return Task.FromResult(RecordedReplies.Tmdb("search-movie-by-provider-id").Single.Reply.ToResponse());
            if (request.RequestUri.AbsolutePath == "/metadata/search" && query.Contains("providerId=999999999", StringComparison.Ordinal))
                return Task.FromResult(RecordedReplies.Tmdb("search-provider-record-missing").Single.Reply.ToResponse());
            throw new InvalidOperationException($"No gateway recording answers {request.Method} {request.RequestUri.PathAndQuery}.");
        }
    }

    private static Task<ApplicationTestHost> StartAsync(GatewayReplay replay)
        => ApplicationTestHost.StartAsync(replaceServices: services =>
            services.AddHttpClient<TmdbMetadataProvider>().ConfigurePrimaryHttpMessageHandler(() => replay));

    private static async Task<JsonElement> PreviewAsync(ApplicationTestHost app, string movieId, string providerId)
    {
        var preview = await app.Client.PostAsJsonAsync($"/api/movies/{movieId}/metadata/link/preview", new { providerId });
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        return await preview.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task A_film_is_linked_to_the_record_the_gateway_really_returned_keeping_its_file_and_a_stale_token_changes_nothing()
    {
        var replay = new GatewayReplay();
        await using var app = await StartAsync(replay);
        var film = await ApiScenes.FilmOnDiskAsync(app, "Sintel Old Name", 2010);
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();

        var preview = await PreviewAsync(app, film.MovieId, "45745");
        Assert.True(preview.GetProperty("canApply").GetBoolean());
        var token = preview.GetProperty("confirmationToken").GetString()!;
        Assert.True(replay.Calls > 0, "the preview read the gateway");

        var stale = await app.Client.PostAsJsonAsync($"/api/movies/{film.MovieId}/metadata/link", new { providerId = "45745", confirmationToken = token + "-changed" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("metadata-link-preview-stale", (await stale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.NotEqual("45745", (await movies.GetByIdAsync(film.MovieId, CancellationToken.None))!.MetadataProviderId);

        var linked = await app.Client.PostAsJsonAsync($"/api/movies/{film.MovieId}/metadata/link", new { providerId = "45745", confirmationToken = token });

        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        var after = (await movies.GetByIdAsync(film.MovieId, CancellationToken.None))!;
        Assert.Equal("45745", after.MetadataProviderId);
        Assert.Equal("Sintel", after.Title);
        Assert.Equal("tt1727587", after.ImdbId);
        Assert.True(File.Exists(film.FilePath), "re-linking never touches the film's file");
    }

    [Fact]
    public async Task A_film_is_not_linked_to_the_record_another_film_already_holds_and_a_record_the_gateway_says_is_missing_changes_nothing()
    {
        var replay = new GatewayReplay();
        await using var app = await StartAsync(replay);
        var first = await ApiScenes.FilmOnDiskAsync(app, "Sintel", 2010);
        var second = await ApiScenes.FilmOnDiskAsync(app, "Another Film", 2011, first.LibraryId);
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        var token = (await PreviewAsync(app, first.MovieId, "45745")).GetProperty("confirmationToken").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await app.Client.PostAsJsonAsync($"/api/movies/{first.MovieId}/metadata/link", new { providerId = "45745", confirmationToken = token })).StatusCode);

        var heldPreview = await PreviewAsync(app, second.MovieId, "45745");
        Assert.False(heldPreview.GetProperty("canApply").GetBoolean());
        var refused = await app.Client.PostAsJsonAsync($"/api/movies/{second.MovieId}/metadata/link", new { providerId = "45745", confirmationToken = heldPreview.GetProperty("confirmationToken").GetString() });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("metadata-link-blocked", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var before = (await movies.GetByIdAsync(second.MovieId, CancellationToken.None))!.MetadataProviderId;
        var missing = await app.Client.PostAsJsonAsync($"/api/movies/{second.MovieId}/metadata/link", new { providerId = "999999999", confirmationToken = "x" });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(before, (await movies.GetByIdAsync(second.MovieId, CancellationToken.None))!.MetadataProviderId);
        Assert.Equal("Another Film", (await movies.GetByIdAsync(second.MovieId, CancellationToken.None))!.Title);
    }
}
