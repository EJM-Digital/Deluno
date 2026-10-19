using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// "No release found" in the Needs action list: dismissing a title stops it being listed and changes nothing about the title; if the
/// title later stops being this problem and then is again, it comes back as a new one. A kind of title that does not exist is a 404.
/// </summary>
public sealed class ATitleWithNoReleaseCanBeDismissedFromTheBellTests
{
    private static async Task<string> WantedFilmAsync(ApplicationTestHost app, string outcome)
    {
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        var added = await movies.AddAsync(new CreateMovieRequest("Nothing Found", 2020, null), CancellationToken.None);
        await movies.EnsureWantedStateAsync(added.Id, "lib", "missing", "Wanted", hasFile: false, null, "WEB 1080p", false, CancellationToken.None);
        await SearchedAsync(app, added.Id, outcome);
        return added.Id;
    }

    private static Task SearchedAsync(ApplicationTestHost app, string movieId, string outcome)
        => app.Services.GetRequiredService<IMovieCatalogRepository>().RecordSearchAttemptAsync(
            movieId, "lib", "automatic", outcome, DateTimeOffset.UtcNow, null, "Searched and found nothing.", null, null, null, CancellationToken.None);

    private static async Task<JsonElement> ListedAsync(ApplicationTestHost app)
        => await app.Client.GetFromJsonAsync<JsonElement>("/api/attention/no-release");

    [Fact]
    public async Task Dismissing_a_title_stops_it_being_listed_and_leaves_it_wanted_and_a_new_search_that_finds_nothing_does_not_bring_it_back()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await WantedFilmAsync(app, "checked");
        Assert.Equal(id, Assert.Single((await ListedAsync(app)).EnumerateArray()).GetProperty("id").GetString());

        var dismissed = await app.Client.PostAsync($"/api/attention/no-release/movie/{id}/dismiss", content: null);

        Assert.Equal(HttpStatusCode.NoContent, dismissed.StatusCode);
        Assert.Empty((await ListedAsync(app)).EnumerateArray());
        var film = await app.Services.GetRequiredService<IMovieCatalogRepository>().GetByIdAsync(id, CancellationToken.None);
        Assert.NotNull(film);
        Assert.True(film!.Monitored);
        Assert.False(film.HasFile);
        await SearchedAsync(app, id, "checked");
        Assert.Empty((await ListedAsync(app)).EnumerateArray());
    }

    [Fact]
    public async Task A_dismissed_title_comes_back_as_a_new_problem_once_a_search_has_found_something_and_a_later_one_finds_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await WantedFilmAsync(app, "checked");
        await app.Client.PostAsync($"/api/attention/no-release/movie/{id}/dismiss", content: null);
        Assert.Empty((await ListedAsync(app)).EnumerateArray());

        await SearchedAsync(app, id, "matched");
        Assert.Empty((await ListedAsync(app)).EnumerateArray());
        await Task.Delay(1100); // reason: the two searches are told apart by their time, which is kept to the second
        await SearchedAsync(app, id, "checked");

        Assert.Equal(id, Assert.Single((await ListedAsync(app)).EnumerateArray()).GetProperty("id").GetString());
    }

    [Fact]
    public async Task Dismissing_a_kind_of_title_that_does_not_exist_is_404_and_changes_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await WantedFilmAsync(app, "checked");

        var response = await app.Client.PostAsync($"/api/attention/no-release/album/{id}/dismiss", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Single((await ListedAsync(app)).EnumerateArray());
    }
}
