using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The add-a-title requests when they are wrong (coverage register, area library, low rows): no title, a library or quality profile that is
/// not the person's, and a profile made for the other kind of title are each refused in a sentence, and nothing is added.
/// </summary>
public sealed class AddingATitleRefusesAWrongRequestLowRowsTests
{
    private static async Task<string[]> ProblemsAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        return errors.GetProperty(field).EnumerateArray().Select(item => item.GetString()!).ToArray();
    }

    [Fact]
    public async Task A_film_with_no_title_or_a_library_or_profile_that_is_not_the_persons_is_refused_in_a_sentence_and_nothing_is_added()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");

        var noTitle = await app.Client.PostAsJsonAsync("/api/movies", new { title = "  ", releaseYear = 2021, monitored = true, libraryId });
        var badLibrary = await app.Client.PostAsJsonAsync("/api/movies", new { title = "Dune", releaseYear = 2021, monitored = true, libraryId = "no-such-library" });
        var badProfile = await app.Client.PostAsJsonAsync("/api/movies", new { title = "Dune", releaseYear = 2021, monitored = true, libraryId, qualityProfileId = "no-such-profile" });

        Assert.Equal(["A movie title is required."], await ProblemsAsync(noTitle, "title"));
        Assert.Equal(["Choose one of your movie libraries."], await ProblemsAsync(badLibrary, "libraryId"));
        Assert.Equal(["Choose one of your movie quality profiles."], await ProblemsAsync(badProfile, "qualityProfileId"));
        Assert.Empty(await app.Services.GetRequiredService<IMovieCatalogRepository>().ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Adding_from_Discover_with_no_title_is_refused_with_A_title_is_required()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");

        var refused = await app.Client.PostAsJsonAsync("/api/discover/add", new { mediaType = "movie", title = " ", year = 1999, provider = "tmdb", providerId = "603", libraryId, monitored = true });

        Assert.Equal(["A title is required."], await ProblemsAsync(refused, "title"));
        Assert.Empty(await app.Services.GetRequiredService<IMovieCatalogRepository>().ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_film_or_show_or_discover_title_over_200_characters_is_refused_in_a_sentence_naming_the_title_and_nothing_is_added()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var movieLibrary = await ApiScenes.CreateLibraryAsync(app, "movies");
        var longTitle = new string('A', 201);

        var film = await app.Client.PostAsJsonAsync("/api/movies", new { title = longTitle, releaseYear = 2021, monitored = true, libraryId = movieLibrary });
        var show = await app.Client.PostAsJsonAsync("/api/series", new { title = longTitle, startYear = 2021, monitored = true });
        var discover = await app.Client.PostAsJsonAsync("/api/discover/add", new { mediaType = "movie", title = longTitle, year = 1999, provider = "tmdb", providerId = "603", libraryId = movieLibrary, monitored = true });

        foreach (var refused in new[] { film, show, discover })
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("The title is too long. It can be at most 200 characters.", body.GetProperty("message").GetString());
        }
        Assert.Empty(await app.Services.GetRequiredService<IMovieCatalogRepository>().ListAsync(CancellationToken.None));
    }
}
