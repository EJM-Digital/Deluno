using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>Low-severity rows (#1329) of the API's front door, on the whole application with its own databases.</summary>
public sealed class ApiRootAndHealthLowGapTests
{
    [Fact]
    public async Task The_API_root_names_Deluno_to_a_signed_in_caller_and_the_live_check_answers_to_anyone()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var root = await app.Client.GetFromJsonAsync<JsonElement>("/api/");
        Assert.Equal("Deluno", root.GetProperty("name").GetString());
        Assert.Equal("media-automation", root.GetProperty("kind").GetString());
        Assert.Equal("sqlite-first modular monolith", root.GetProperty("architecture").GetString());

        using var anonymous = app.CreateAnonymousClient();
        var live = await anonymous.GetAsync("/api/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("live", (await live.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("download-dispatches")]
    [InlineData("import-resolutions")]
    [InlineData("release-preferences/plans")]
    [InlineData("guides/trash/package")]
    public async Task A_dedicated_v1_route_is_answered_by_its_own_handler_and_not_rewritten_to_a_bare_path(string route)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.GetAsync($"/api/v1/{route}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }
}
