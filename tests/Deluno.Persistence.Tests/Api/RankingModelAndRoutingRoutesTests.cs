using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The ranking-model and intelligent-routing routes on the real host, on a fresh install: the model is switched off, so
/// its status says so and training and rolling back are refused in plain words, and routing reads an empty history.
/// </summary>
public sealed class RankingModelAndRoutingRoutesTests
{
    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [Fact]
    public async Task The_model_status_on_a_fresh_install_says_it_is_off_offline_with_no_versions()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var status = await app.Client.GetFromJsonAsync<JsonElement>("/api/ranking-model/status");

        Assert.False(status.GetProperty("enabled").GetBoolean());
        Assert.False(status.GetProperty("modelLoaded").GetBoolean());
        Assert.Equal("offline", status.GetProperty("mode").GetString());
        Assert.Equal(0, status.GetProperty("trainingSampleCount").GetInt32());
        Assert.Equal(0, status.GetProperty("availableVersions").GetArrayLength());
    }

    [Fact]
    public async Task Training_a_model_that_is_switched_off_is_a_400_that_says_so_and_trains_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsync("/api/ranking-model/train", Json("{}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Contains("disabled", body.GetProperty("message").GetString());
        Assert.Equal(0, body.GetProperty("sampleCount").GetInt32());
        var status = await app.Client.GetFromJsonAsync<JsonElement>("/api/ranking-model/status");
        Assert.Equal(0, status.GetProperty("availableVersions").GetArrayLength());
    }

    [Theory]
    [InlineData("{}", "A target version is required.")]
    [InlineData("{\"version\":\"never-trained\"}", "")]
    public async Task Rolling_back_without_a_version_or_to_one_that_never_existed_is_refused_and_changes_nothing(string json, string message)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsync("/api/ranking-model/rollback", Json(json));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("accepted").GetBoolean());
        Assert.Contains(message, body.GetProperty("message").GetString());
        var status = await app.Client.GetFromJsonAsync<JsonElement>("/api/ranking-model/status");
        Assert.Null(status.GetProperty("activeModelVersion").GetString());
    }

    [Fact]
    public async Task Routing_on_a_fresh_install_reads_an_empty_history_and_no_unusual_patterns()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var snapshot = await app.Client.GetFromJsonAsync<JsonElement>("/api/intelligent-routing/snapshot");
        var anomalies = await app.Client.GetFromJsonAsync<JsonElement>("/api/intelligent-routing/anomalies");

        Assert.Empty(snapshot.GetProperty("indexerSuccessRates").EnumerateObject());
        Assert.Empty(snapshot.GetProperty("downloadClientSuccessRates").EnumerateObject());
        Assert.Equal(0, snapshot.GetProperty("preferences").GetProperty("preferredReleaseGroups").GetArrayLength());
        Assert.Equal(0, anomalies.GetProperty("count").GetInt32());
        Assert.Equal(0, anomalies.GetProperty("anomalies").GetArrayLength());
    }

    [Fact]
    public async Task A_release_recommendation_with_no_history_is_scored_with_the_model_off_and_says_nothing_was_boosted()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsync("/api/intelligent-routing/recommend-release", Json("{}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var score = body.GetProperty("recommendationScore").GetInt32();
        Assert.InRange(score, 0, 100);
        Assert.False(body.GetProperty("rankingBoost").GetProperty("enabled").GetBoolean());
        Assert.False(body.GetProperty("rankingBoost").GetProperty("applied").GetBoolean());
        Assert.Equal(0, body.GetProperty("rankingBoost").GetProperty("boostPoints").GetInt32());
        Assert.True(body.GetProperty("indexerSuccessRate").ValueKind == JsonValueKind.Null);
    }

    [Theory]
    [InlineData("/api/ranking-model/status")]
    [InlineData("/api/intelligent-routing/snapshot")]
    [InlineData("/api/intelligent-routing/anomalies")]
    public async Task The_read_routes_need_a_sign_in(string route)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateAnonymousClient().GetAsync(route)).StatusCode);
    }
}
