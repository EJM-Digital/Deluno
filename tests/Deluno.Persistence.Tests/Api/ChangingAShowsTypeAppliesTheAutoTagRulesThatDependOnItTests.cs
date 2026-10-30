using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// A rule can depend on the series type (anime, daily, standard), so saving a new type on the show's numbering applies the
/// rules again: the show gets the tag of the rule its new type matches and a rule it no longer matches takes its tag away.
/// Real host and databases.
/// </summary>
public sealed class ChangingAShowsTypeAppliesTheAutoTagRulesThatDependOnItTests
{
    [Fact]
    public async Task A_show_changed_to_anime_gets_the_anime_rules_tag_and_loses_it_when_changed_back()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var show = await ApiScenes.ShowOnDiskAsync(app, "One Piece", 1999);
        using var rule = await app.Client.PostAsJsonAsync("/api/auto-tag-rules", new
        {
            name = "Anime shows",
            tags = new[] { "anime-shows" },
            removeAutomatically = true,
            appliesTo = "tv",
            isEnabled = true,
            specifications = new object[] { new { type = "seriesType", negate = false, value = "anime" } }
        });
        Assert.True(rule.IsSuccessStatusCode, await rule.Content.ReadAsStringAsync());

        async Task<string[]> TagsAsync() => (await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/{show.SeriesId}/tags"))
            .EnumerateArray().Select(tag => tag.GetProperty("name").GetString()!).ToArray();

        Assert.DoesNotContain("anime-shows", await TagsAsync());

        using var toAnime = await app.Client.PutAsJsonAsync($"/api/series/{show.SeriesId}/numbering", new { seriesType = "anime" });
        Assert.Equal(HttpStatusCode.OK, toAnime.StatusCode);
        Assert.Contains("anime-shows", await TagsAsync());

        using var backToStandard = await app.Client.PutAsJsonAsync($"/api/series/{show.SeriesId}/numbering", new { seriesType = "standard" });
        Assert.Equal(HttpStatusCode.OK, backToStandard.StatusCode);
        Assert.DoesNotContain("anime-shows", await TagsAsync());
    }
}
