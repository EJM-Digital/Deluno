using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// A tag that a working indexer carries cannot be deleted (#1292): the indexer is a real Torznab one (Jackett 0.24.2850, its
/// recorded replies served by a local server), tested and healthy, so the refusal is not about a dead stand-in.
/// The server says who uses the tag and keeps the tag until the indexer lets go.
/// </summary>
public sealed class TagInUseByARecordedIndexerTests
{
    [Fact]
    public async Task A_tag_on_a_tested_working_indexer_is_refused_with_who_uses_it_and_can_be_deleted_once_the_indexer_lets_go()
    {
        await using var server = await RecordedIndexerServer.StartAsync(RecordedReplies.Jackett("search-text"), RecordedReplies.Jackett("caps"));
        await using var app = await ApplicationTestHost.StartAsync();
        var tag = (await (await app.Client.PostAsJsonAsync("/api/tags", new { name = "Anime", color = "slate", description = "" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var created = await app.Client.PostAsJsonAsync("/api/indexers", new
        {
            name = "Jackett YTS", privacy = "public", priority = 10, categories = "2000", tags = "Anime", mediaScope = "movies", isEnabled = true,
            protocol = "torznab", baseUrl = server.BaseUrl + "/api/v2.0/indexers/yts/results/torznab/api", apiKey = "k-secret-1"
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var indexerId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var tested = await (await app.Client.PostAsync($"/api/indexers/{indexerId}/test", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("healthy", tested.GetProperty("healthStatus").GetString());

        var refused = await app.Client.DeleteAsync($"/api/tags/{tag}");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var message = (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString()!;
        Assert.Contains("still in use by 1 indexer", message);
        Assert.Contains("Remove it from those first.", message);
        var tags = await app.Client.GetFromJsonAsync<JsonElement>("/api/tags");
        Assert.Contains(tags.EnumerateArray(), item => item.GetProperty("id").GetString() == tag);

        Assert.True((await app.Client.DeleteAsync($"/api/indexers/{indexerId}")).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/tags/{tag}")).StatusCode);
    }
}
