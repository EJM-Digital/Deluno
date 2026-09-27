using Deluno.Persistence.Tests.Support;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #843's per-title subtitle-language override, through the real route
/// table, for both catalogues.
/// </summary>
public sealed class SubtitleLanguageOverrideApiTests
{
    [Theory]
    [InlineData("movies", "/api/movies/")]
    [InlineData("tv", "/api/series/")]
    public async Task Setting_an_override_is_returned_on_the_titles_own_fetch(string mediaType, string route)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await AddAsync(app, route, mediaType);

        var set = await app.Client.PutAsJsonAsync(
            $"{route.TrimEnd('/')}/{id}/subtitles/override", new { languages = new[] { "ja", "en" }, mode = "first" });
        Assert.True(set.IsSuccessStatusCode, await set.Content.ReadAsStringAsync());

        var get = await app.Client.GetAsync($"{route.TrimEnd('/')}/{id}");
        using var document = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        var body = document.RootElement;

        var languages = body.GetProperty("subtitleLanguagesOverride").EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();
        Assert.Equal(["ja", "en"], languages);
        Assert.Equal("first", body.GetProperty("subtitleModeOverride").GetString());
    }

    [Theory]
    [InlineData("movies", "/api/movies/")]
    [InlineData("tv", "/api/series/")]
    public async Task A_title_with_no_override_returns_null_for_both_fields(string mediaType, string route)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await AddAsync(app, route, mediaType);

        var get = await app.Client.GetAsync($"{route.TrimEnd('/')}/{id}");
        using var document = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        var body = document.RootElement;

        Assert.Equal(JsonValueKind.Null, body.GetProperty("subtitleLanguagesOverride").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("subtitleModeOverride").ValueKind);
    }

    [Theory]
    [InlineData("movies", "/api/movies/")]
    [InlineData("tv", "/api/series/")]
    public async Task Clearing_an_override_goes_back_to_no_override(string mediaType, string route)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await AddAsync(app, route, mediaType);

        await app.Client.PutAsJsonAsync(
            $"{route.TrimEnd('/')}/{id}/subtitles/override", new { languages = new[] { "ja" }, mode = "all" });

        var deleted = await app.Client.DeleteAsync($"{route.TrimEnd('/')}/{id}/subtitles/override");
        Assert.True(deleted.IsSuccessStatusCode, await deleted.Content.ReadAsStringAsync());

        var get = await app.Client.GetAsync($"{route.TrimEnd('/')}/{id}");
        using var document = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("subtitleLanguagesOverride").ValueKind);
    }

    [Theory]
    [InlineData("/api/movies/")]
    [InlineData("/api/series/")]
    public async Task Setting_an_override_with_no_languages_is_a_validation_problem(string route)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await AddAsync(app, route, route == "/api/series/" ? "tv" : "movies");

        var response = await app.Client.PutAsJsonAsync(
            $"{route.TrimEnd('/')}/{id}/subtitles/override", new { languages = Array.Empty<string>(), mode = "all" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<string> AddAsync(ApplicationTestHost app, string route, string mediaType)
    {
        object body = mediaType == "tv"
            ? new { title = "Severance", startYear = 2022, monitored = true }
            : new { title = "Arrival", releaseYear = 2016, monitored = true };

        var response = await app.Client.PostAsJsonAsync(route, body);
        Assert.True(
            response.IsSuccessStatusCode,
            $"POST {route} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }
}
