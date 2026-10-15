using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The ranking-model routes on a stock install (coverage register, area engines): the model is off, so training says so with a 400 and nothing is trained, and a
/// rollback with no version or a version nobody trained is a 400 with the reason and changes nothing.
/// </summary>
public sealed class RankingModelRoutesRefuseInPlainWordsTests
{
    [Fact]
    public async Task Training_on_an_install_where_the_model_is_off_is_a_400_that_says_so_and_nothing_is_trained()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsync("/api/ranking-model/train", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("Ranking model is disabled in configuration.", body.GetProperty("message").GetString());
        var status = await app.Client.GetFromJsonAsync<JsonElement>("/api/ranking-model/status");
        Assert.False(status.GetProperty("enabled").GetBoolean());
        Assert.False(status.GetProperty("modelLoaded").GetBoolean());
    }

    [Theory]
    [InlineData("", "A target version is required.")]
    [InlineData("rm-never-trained", "Model version 'rm-never-trained' was not found.")]
    public async Task A_rollback_to_no_version_or_one_that_is_not_stored_is_a_400_with_the_reason(string version, string message)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/ranking-model/rollback", new { version });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("accepted").GetBoolean());
        Assert.Equal(message, body.GetProperty("message").GetString());
        Assert.False((await app.Client.GetFromJsonAsync<JsonElement>("/api/ranking-model/status")).GetProperty("modelLoaded").GetBoolean());
    }
}
