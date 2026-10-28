using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1329: the show shelf's read routes, asked about shows that really are in the catalogue (three that differ in title and start year, put
/// there through the real import): the paged list sorts, filters, pages and counts them; the compact list names the shows and the episode
/// asked for and leaves a gone one out; the posters answer for the ids asked; the wanted summary counts what is held.
/// </summary>
public sealed class TheShowShelfRoutesReadWhatIsKeptTests
{
    private static async Task<(ApplicationTestHost App, ApiScenes.Show Wire, ApiScenes.Show Dark, ApiScenes.Show Fargo)> ThreeShowsAsync()
    {
        var app = await ApplicationTestHost.StartAsync();
        var library = await ApiScenes.CreateLibraryAsync(app, "tv");
        var wire = await ApiScenes.ShowOnDiskAsync(app, "The Wire", 2002, library);
        var dark = await ApiScenes.ShowOnDiskAsync(app, "Dark", 2017, library);
        var fargo = await ApiScenes.ShowOnDiskAsync(app, "Fargo", 2014, library);
        return (app, wire, dark, fargo);
    }

    private static string[] Titles(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("title").GetString()!).ToArray();

    [Fact]
    public async Task The_paged_show_list_sorts_filters_pages_and_counts_the_shows_in_the_catalogue()
    {
        var (app, _, _, _) = await ThreeShowsAsync();
        await using var _app = app;

        var byTitle = await app.Client.GetFromJsonAsync<JsonElement>("/api/series/page?sort=title&direction=asc");
        Assert.Equal(["Dark", "Fargo", "The Wire"], Titles(byTitle));
        Assert.Equal(3, byTitle.GetProperty("totalCount").GetInt32());

        var reversed = await app.Client.GetFromJsonAsync<JsonElement>("/api/series/page?sort=title&direction=desc");
        Assert.Equal(["The Wire", "Fargo", "Dark"], Titles(reversed));

        var searched = await app.Client.GetFromJsonAsync<JsonElement>("/api/series/page?search=farg");
        Assert.Equal(["Fargo"], Titles(searched));
        Assert.Equal(1, searched.GetProperty("totalCount").GetInt32());

        var recent = await app.Client.GetFromJsonAsync<JsonElement>("/api/series/page?minYear=2010&sort=title&direction=asc");
        Assert.Equal(["Dark", "Fargo"], Titles(recent));

        var first = await app.Client.GetFromJsonAsync<JsonElement>("/api/series/page?sort=title&direction=asc&pageSize=2");
        Assert.Equal(["Dark", "Fargo"], Titles(first));
        Assert.True(first.GetProperty("hasMore").GetBoolean());
        var second = await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/page?sort=title&direction=asc&pageSize=2&pageToken={Uri.EscapeDataString(first.GetProperty("nextPageToken").GetString()!)}");
        Assert.Equal(["The Wire"], Titles(second));
        Assert.False(second.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task The_compact_lists_name_the_shows_and_the_episode_asked_for_and_leave_a_gone_one_out()
    {
        var (app, wire, dark, _) = await ThreeShowsAsync();
        await using var _app = app;

        var titles = await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/titles?ids={wire.SeriesId},no-such-show&episodeIds={dark.EpisodeId}");
        var shows = titles.GetProperty("series");
        Assert.Equal("The Wire", shows.GetProperty(wire.SeriesId).GetProperty("title").GetString());
        Assert.Equal(2002, shows.GetProperty(wire.SeriesId).GetProperty("year").GetInt32());
        Assert.False(shows.TryGetProperty("no-such-show", out _));
        Assert.Equal("Dark", shows.GetProperty(dark.SeriesId).GetProperty("title").GetString());
        var episode = titles.GetProperty("episodes").GetProperty(dark.EpisodeId);
        Assert.Equal(dark.SeriesId, episode.GetProperty("seriesId").GetString());
        Assert.Equal(1, episode.GetProperty("season").GetInt32());
        Assert.Equal(1, episode.GetProperty("episode").GetInt32());

        var posters = await app.Client.GetFromJsonAsync<JsonElement>($"/api/series/posters?ids={wire.SeriesId},no-such-show");
        Assert.True(posters.TryGetProperty(wire.SeriesId, out _));
        Assert.False(posters.TryGetProperty("no-such-show", out _));
    }

    [Fact]
    public async Task The_wanted_summary_sums_the_shows_by_their_wanted_status_and_lists_the_newest()
    {
        var (app, _, _, _) = await ThreeShowsAsync();
        await using var _app = app;
        var series = app.Services.GetRequiredService<ISeriesCatalogRepository>();
        var libraryId = (await app.Client.GetFromJsonAsync<JsonElement>("/api/libraries")).EnumerateArray().First().GetProperty("id").GetString()!;
        await series.ImportExistingAsync(libraryId, "Lost Show", 2010, "missing", "Not here yet.", null, "Bluray-1080p", false, null, null,
            [new ImportedEpisodeItem(1, 1, HasFile: false, FilePath: null, FileSizeBytes: null)], CancellationToken.None);

        var wanted = await app.Client.GetFromJsonAsync<JsonElement>("/api/series/wanted");

        // The import stores each show as missing until its episodes are catalogued, so all four are counted, none as an upgrade.
        Assert.Equal(4, wanted.GetProperty("totalWanted").GetInt32());
        Assert.Equal(4, wanted.GetProperty("missingCount").GetInt32());
        Assert.Equal(0, wanted.GetProperty("upgradeCount").GetInt32());
        var titles = wanted.GetProperty("recentItems").EnumerateArray().Select(item => item.GetProperty("title").GetString()!).Order().ToArray();
        Assert.Equal(["Dark", "Fargo", "Lost Show", "The Wire"], titles);
    }
}
