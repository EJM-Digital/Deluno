using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Quality.Contracts;

namespace Deluno.Persistence.Tests.Api;

public sealed class CustomFormatNameApiTests
{
    [Fact]
    public async Task Duplicate_rule_names_are_rejected_within_one_media_type()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var request = new CreateCustomFormatRequest(
            "Preferred DELUNO group",
            "movies",
            0,
            null,
            "[{\"type\":\"releaseGroup\",\"value\":\"DELUNO\",\"required\":true}]",
            true);

        var first = await app.Client.PostAsJsonAsync("/api/custom-formats", request);
        var duplicate = await app.Client.PostAsJsonAsync(
            "/api/custom-formats",
            request with { Name = "  preferred deluno GROUP  " });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        using var body = JsonDocument.Parse(await duplicate.Content.ReadAsStreamAsync());
        var message = Assert.Single(body.RootElement
            .GetProperty("errors")
            .GetProperty("name")
            .EnumerateArray()).GetString();
        Assert.Contains("already exists", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Edit that rule", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_same_rule_name_can_be_owned_independently_by_movies_and_tv()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var movie = new CreateCustomFormatRequest(
            "Preferred DELUNO group",
            "movies",
            0,
            null,
            "DELUNO",
            true);

        var movieResponse = await app.Client.PostAsJsonAsync("/api/custom-formats", movie);
        var tvResponse = await app.Client.PostAsJsonAsync(
            "/api/custom-formats",
            movie with { MediaType = "tv" });

        Assert.Equal(HttpStatusCode.OK, movieResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, tvResponse.StatusCode);
    }
}
