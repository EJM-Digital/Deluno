using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// POST /api/movies/bulk is still exposed although the screen now uses the per-purpose routes: its monitoring and quality
/// operations change every chosen film and say plainly which could not be done, and an operation it does not have ("grab") or
/// a film that is not there is reported against that film instead of failing the call. Real host and databases.
/// </summary>
public sealed class TheBulkEditorsOtherOperationsTests
{
    [Fact]
    public async Task The_monitoring_operation_sets_every_chosen_film_and_asks_for_the_state_when_it_is_missing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var one = await ApiScenes.FilmOnDiskAsync(app, "Bulk One", 2020);
        var two = await ApiScenes.FilmOnDiskAsync(app, "Bulk Two", 2021, one.LibraryId, folderName: "Bulk Two (2021)");

        using var off = await app.Client.PostAsJsonAsync("/api/movies/bulk", new { movieIds = new[] { one.MovieId, two.MovieId }, operation = "monitoring", monitored = false });
        var offBody = await off.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.Equal(2, offBody.GetProperty("successCount").GetInt32());
        Assert.False((await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{one.MovieId}")).GetProperty("monitored").GetBoolean());
        Assert.False((await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{two.MovieId}")).GetProperty("monitored").GetBoolean());

        using var unsaid = await app.Client.PostAsJsonAsync("/api/movies/bulk", new { movieIds = new[] { one.MovieId }, operation = "monitoring" });
        var unsaidBody = await unsaid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, unsaidBody.GetProperty("failureCount").GetInt32());
        Assert.Equal("Monitored state must be specified for monitoring operation", unsaidBody.GetProperty("results")[0].GetProperty("errorMessage").GetString());
    }

    [Fact]
    public async Task The_quality_operation_asks_for_a_profile_and_a_grab_or_a_missing_film_is_reported_against_that_film()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app, "Bulk Three", 2022);

        using var noProfile = await app.Client.PostAsJsonAsync("/api/movies/bulk", new { movieIds = new[] { film.MovieId }, operation = "quality" });
        var noProfileBody = await noProfile.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, noProfileBody.GetProperty("failureCount").GetInt32());
        Assert.Equal("Quality profile ID must be specified for quality operation", noProfileBody.GetProperty("results")[0].GetProperty("errorMessage").GetString());

        using var grab = await app.Client.PostAsJsonAsync("/api/movies/bulk", new { movieIds = new[] { film.MovieId, "no-such-film" }, operation = "grab" });
        var grabBody = await grab.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, grab.StatusCode);
        Assert.Equal(2, grabBody.GetProperty("failureCount").GetInt32());
        var errors = grabBody.GetProperty("results").EnumerateArray().Select(item => item.GetProperty("errorMessage").GetString()).ToArray();
        Assert.Contains("Unknown operation: grab", errors);
        Assert.Contains("Movie not found", errors);

        using var none = await app.Client.PostAsJsonAsync("/api/movies/bulk", new { movieIds = Array.Empty<string>(), operation = "monitoring", monitored = true });
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateAnonymousClient().PostAsJsonAsync("/api/movies/bulk", new { movieIds = new[] { film.MovieId }, operation = "monitoring", monitored = true })).StatusCode);
    }
}
