using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The library-membership routes (#530, #1065) in the cases LibraryMembershipTests does not walk: what each refuses and says,
/// the "Want it again" answer, a show's own add and move, and the change that tells an open title page to refresh.
/// </summary>
public sealed class LibraryMembershipRoutesRefuseAndAnnounceTests
{
    private static async Task<(ApplicationTestHost App, RecordingRealtimeEventPublisher Publisher)> StartAsync()
    {
        var publisher = new RecordingRealtimeEventPublisher();
        var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.Replace(ServiceDescriptor.Singleton<IRealtimeEventPublisher>(publisher)));
        return (app, publisher);
    }

    private static async Task<string> AddFilmAsync(ApplicationTestHost app, string title, int year, string libraryId)
    {
        var response = await app.Client.PostAsJsonAsync("/api/movies/", new { title, releaseYear = year, monitored = true, libraryId });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<string> AddShowAsync(ApplicationTestHost app, string title, string? libraryId = null)
    {
        var response = await app.Client.PostAsJsonAsync("/api/series/", new { title, startYear = 2020, monitored = true, libraryId });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<string[]> LibrariesOfAsync(ApplicationTestHost app, string kind, string id)
        => (await app.Client.GetFromJsonAsync<JsonElement>($"/api/{kind}/{id}/libraries/")).EnumerateArray().Select(item => item.GetProperty("libraryId").GetString()!).Order().ToArray();

    [Fact]
    public async Task Adding_a_film_to_a_library_refuses_an_unknown_title_404_and_a_missing_or_wrong_type_library_with_choose_one_of_your_film_libraries()
    {
        var (app, _) = await StartAsync();
        await using var _app = app;
        var films = await ApiScenes.CreateLibraryAsync(app, "movies");
        var shows = await ApiScenes.CreateLibraryAsync(app, "tv");
        var id = await AddFilmAsync(app, "Arrival", 2016, films);

        var unknown = await app.Client.PostAsJsonAsync("/api/movies/no-such-film/libraries/", new { libraryId = films });
        var missing = await app.Client.PostAsJsonAsync($"/api/movies/{id}/libraries/", new { libraryId = (string?)null });
        var wrongType = await app.Client.PostAsJsonAsync($"/api/movies/{id}/libraries/", new { libraryId = shows });

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, wrongType.StatusCode);
        Assert.Contains("Choose one of your film libraries.", await missing.Content.ReadAsStringAsync());
        Assert.Contains("Choose one of your film libraries.", await wrongType.Content.ReadAsStringAsync());
        Assert.Equal([films], await LibrariesOfAsync(app, "movies", id));
    }

    [Fact]
    public async Task Moving_a_title_refuses_a_library_it_is_already_in_a_library_of_the_wrong_type_a_source_it_is_not_in_and_an_unknown_title()
    {
        var (app, _) = await StartAsync();
        await using var _app = app;
        var hd = await ApiScenes.CreateLibraryAsync(app, "movies", "Films HD", "Films HD");
        var uhd = await ApiScenes.CreateLibraryAsync(app, "movies", "Films 4K", "Films 4K");
        var shows = await ApiScenes.CreateLibraryAsync(app, "tv");
        var id = await AddFilmAsync(app, "Arrival", 2016, hd);
        (await app.Client.PostAsJsonAsync($"/api/movies/{id}/libraries/", new { libraryId = uhd })).EnsureSuccessStatusCode();

        var already = await app.Client.PutAsJsonAsync($"/api/movies/{id}/libraries/{hd}", new { libraryId = uhd });
        var wrongType = await app.Client.PutAsJsonAsync($"/api/movies/{id}/libraries/{hd}", new { libraryId = shows });
        var notIn = await app.Client.PutAsJsonAsync($"/api/movies/{id}/libraries/{shows}", new { libraryId = uhd });
        var unknown = await app.Client.PutAsJsonAsync($"/api/movies/no-such-film/libraries/{hd}", new { libraryId = uhd });

        Assert.Equal(HttpStatusCode.Conflict, already.StatusCode);
        Assert.Contains("is already in Films 4K.", await already.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, wrongType.StatusCode);
        Assert.Contains("Choose one of your film libraries.", await wrongType.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, notIn.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(new[] { hd, uhd }.Order().ToArray(), await LibrariesOfAsync(app, "movies", id));
    }

    [Fact]
    public async Task A_show_is_added_into_the_chosen_library_or_the_first_stays_put_when_added_again_is_refused_a_second_library_and_can_be_moved()
    {
        var (app, _) = await StartAsync();
        await using var _app = app;
        var first = await ApiScenes.CreateLibraryAsync(app, "tv", "A Shows", "A Shows");
        var second = await ApiScenes.CreateLibraryAsync(app, "tv", "B Shows", "B Shows");

        var chosen = await AddShowAsync(app, "Severance", second);
        var unchosen = await AddShowAsync(app, "Silo");
        await AddShowAsync(app, "Severance");
        var secondLibrary = await app.Client.PostAsJsonAsync($"/api/series/{unchosen}/libraries/", new { libraryId = second });
        var moved = await app.Client.PutAsJsonAsync($"/api/series/{unchosen}/libraries/{first}", new { libraryId = second });

        Assert.Equal([second], await LibrariesOfAsync(app, "series", chosen));
        Assert.Equal(HttpStatusCode.Conflict, secondLibrary.StatusCode);
        Assert.Contains("A show is in one library. Move it to another library instead.", await secondLibrary.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal([second], await LibrariesOfAsync(app, "series", unchosen));
    }

    [Fact]
    public async Task Want_it_again_wants_the_film_again_in_that_library_writes_the_Activity_line_returns_the_list_and_a_second_ask_is_a_409()
    {
        var (app, _) = await StartAsync();
        await using var _app = app;
        var film = await ApiScenes.FilmOnDiskAsync(app, "Arrival", 2016);
        var uhd = await ApiScenes.CreateLibraryAsync(app, "movies", "Films 4K", "Films 4K");
        (await app.Client.PostAsJsonAsync($"/api/movies/{film.MovieId}/libraries/", new { libraryId = uhd })).EnsureSuccessStatusCode();
        // The second library holds a copy of its own, so losing this one leaves the film wanted there (#1065).
        await ApiScenes.ExecuteAsync(app, "movies", $"UPDATE movie_wanted_state SET has_file = 1, file_path = '{Path.Combine(app.DataRoot, "Films 4K", "Arrival.mkv").Replace("'", "''")}' WHERE movie_id = '{film.MovieId}' AND library_id = '{uhd}';");
        File.Delete(film.FilePath);
        var cleared = await app.Services.GetRequiredService<IMovieCatalogRepository>().ClearFileAsync(film.MovieId, film.FilePath, CancellationToken.None);
        Assert.True(cleared.OnlyThatLibrary, "with a second library the film stops being wanted only where its file went");

        var unknown = await app.Client.PostAsync("/api/movies/no-such-film/libraries/x/want-again", null);
        var again = await app.Client.PostAsync($"/api/movies/{film.MovieId}/libraries/{film.LibraryId}/want-again", null);
        var twice = await app.Client.PostAsync($"/api/movies/{film.MovieId}/libraries/{film.LibraryId}/want-again", null);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var list = await again.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(list.EnumerateArray().Single(item => item.GetProperty("libraryId").GetString() == film.LibraryId).GetProperty("notWantedHere").GetBoolean());
        var activity = await app.Client.GetStringAsync("/api/activity?take=50");
        Assert.Contains("library.movie.wanted-again", activity);
        Assert.Contains("Arrival is wanted again in Films, so Deluno will look for it there.", activity);
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.Contains("Deluno already wants this film in that library.", await twice.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Every_membership_change_tells_the_open_title_page_to_refresh()
    {
        var (app, publisher) = await StartAsync();
        await using var _app = app;
        var hd = await ApiScenes.CreateLibraryAsync(app, "movies", "Films HD", "Films HD");
        var uhd = await ApiScenes.CreateLibraryAsync(app, "movies", "Films 4K", "Films 4K");
        var archive = await ApiScenes.CreateLibraryAsync(app, "movies", "Films Archive", "Films Archive");
        var id = await AddFilmAsync(app, "Arrival", 2016, hd);
        var before = publisher.Published.Count(item => item == ("Movie", id));

        (await app.Client.PostAsJsonAsync($"/api/movies/{id}/libraries/", new { libraryId = uhd })).EnsureSuccessStatusCode();
        var afterAdd = publisher.Published.Count(item => item == ("Movie", id));
        (await app.Client.PutAsJsonAsync($"/api/movies/{id}/libraries/{uhd}", new { libraryId = archive })).EnsureSuccessStatusCode();
        var afterMove = publisher.Published.Count(item => item == ("Movie", id));
        (await app.Client.DeleteAsync($"/api/movies/{id}/libraries/{archive}")).EnsureSuccessStatusCode();
        var afterRemove = publisher.Published.Count(item => item == ("Movie", id));

        Assert.True(afterAdd > before, "adding to a library pushes a Movie change for that title");
        Assert.True(afterMove > afterAdd, "moving pushes one");
        Assert.True(afterRemove > afterMove, "removing a copy pushes one");
    }
}
