using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Indexer History after a search against a Jackett Torznab feed (its recorded replies): renaming the indexer shows its earlier
/// rows under the new name, and deleting it leaves the rows under the name it had.
/// </summary>
public sealed class ARenamedOrDeletedIndexerKeepsItsHistoryOnRecordedIndexersTests
{
    private const string TorznabPath = "/api/v2.0/indexers/yts/results/torznab/api";

    private static async Task AssertOnlyNameAsync(ApplicationTestHost app, string expected)
    {
        var raw = await app.Client.GetStringAsync("/api/indexer-history");
        var names = JsonDocument.Parse(raw).RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("indexerName").GetString()!).Distinct().ToArray();
        Assert.True(names.Length == 1 && names[0] == expected, "wanted only " + expected + " but history is " + raw);
    }

    [Fact]
    public async Task A_renamed_indexer_shows_its_earlier_rows_under_its_new_name_and_a_deleted_one_keeps_the_name_it_had()
    {
        await using var feed = await RecordedIndexerServer.StartAsync(RecordedReplies.Jackett("search-text"), RecordedReplies.Jackett("caps"));
        await using var app = await ApplicationTestHost.StartAsync();
        var created = await app.Client.PostAsJsonAsync("/api/indexers", new
        {
            name = "Old name", protocol = "torznab", privacy = "public", baseUrl = feed.BaseUrl + TorznabPath, apiKey = "0123456789abcdef0123456789abcdef",
            priority = 25, categories = "2000", tags = "", mediaScope = "movies", isEnabled = true, preferMagnetUrl = true
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var search = await app.Client.PostAsJsonAsync("/api/search", new { query = "Sintel", searchType = "movie", indexerIds = new[] { id } });
        Assert.True(search.IsSuccessStatusCode, await search.Content.ReadAsStringAsync());
        await AssertOnlyNameAsync(app, "Old name");

        var renamed = await app.Client.PutAsJsonAsync($"/api/indexers/{id}", new { name = "New name" });
        Assert.True(renamed.IsSuccessStatusCode, await renamed.Content.ReadAsStringAsync());
        Assert.Equal("New name", (await renamed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());

        await AssertOnlyNameAsync(app, "New name");

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/indexers/{id}")).StatusCode);

        // A deleted indexer is no longer on the list to rename, so its rows keep the name they were written with.
        await AssertOnlyNameAsync(app, "Old name");
    }
}
