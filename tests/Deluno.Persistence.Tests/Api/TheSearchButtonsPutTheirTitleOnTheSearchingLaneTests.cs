using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1442: the searches a person starts from a button, which run straight from the request and not as jobs (a film's or a
/// show's Search, Search season, Search episode), put their title on Live's Searching lane while the indexers are asked and
/// take it off when the search ends, each pushed as <c>SearchesInFlightChanged</c> with the whole list. The routes are the
/// real ones on the real host.
/// </summary>
public sealed class TheSearchButtonsPutTheirTitleOnTheSearchingLaneTests
{
    private static async Task<(ApplicationTestHost App, RecordingRealtimeEventPublisher Pushes)> StartAsync()
    {
        var pushes = new RecordingRealtimeEventPublisher();
        var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.AddSingleton(new SearchesInFlight(TimeProvider.System, pushes));
        });
        return (app, pushes);
    }

    /// <summary>The lists pushed so far, in order: the lane at each change.</summary>
    private static IReadOnlyList<IReadOnlyList<SearchingTitle>> Lists(RecordingRealtimeEventPublisher pushes)
        => pushes.LiveEventsOf<SearchesInFlightChangedEvent>(LiveEventNames.SearchesInFlightChanged).Select(item => item.Searches).ToArray();

    private static void AssertOnTheLaneThenGone(RecordingRealtimeEventPublisher pushes, string entityType, string entityId, string title, string label)
    {
        var lists = Lists(pushes);
        var card = Assert.Single(lists[0]);
        Assert.Equal((entityType, entityId, title, label, "manual"), (card.EntityType, card.EntityId, card.Title, card.Label, card.StartedBy));
        Assert.Empty(lists[^1]);
        Assert.True(lists.Count >= 2, "the lane was pushed when the search began and again when it ended");
    }

    [Fact]
    public async Task A_films_Search_button_puts_the_film_on_the_lane_and_takes_it_off_when_the_search_ends()
    {
        var (app, pushes) = await StartAsync();
        await using var _ = app;
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies", "Films", "Films");
        var added = await app.Client.PostAsJsonAsync("/api/movies/", new { title = "Tenet", releaseYear = 2020, monitored = true, libraryId, searchForMissingNow = false });
        var id = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var response = await app.Client.PostAsync($"/api/movies/{id}/search?libraryId={libraryId}", null);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        AssertOnTheLaneThenGone(pushes, "movie", id, "Tenet", "Looking for it");
    }

    [Fact]
    public async Task A_shows_Search_button_puts_the_show_on_the_lane_and_takes_it_off_when_the_search_ends()
    {
        var (app, pushes) = await StartAsync();
        await using var _ = app;
        var show = await ApiScenes.ShowOnDiskAsync(app, "The Wire", 2002);

        var response = await app.Client.PostAsync($"/api/series/{show.SeriesId}/search?libraryId={show.LibraryId}", null);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        AssertOnTheLaneThenGone(pushes, "series", show.SeriesId, "The Wire", "The whole show");
    }

    [Fact]
    public async Task Search_season_puts_the_show_on_the_lane_as_that_season_and_takes_it_off_when_the_search_ends()
    {
        var (app, pushes) = await StartAsync();
        await using var _ = app;
        var show = await ApiScenes.ShowOnDiskAsync(app, "The Wire", 2002);

        var response = await app.Client.PostAsync($"/api/series/{show.SeriesId}/seasons/1/search", null);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        AssertOnTheLaneThenGone(pushes, "series", show.SeriesId, "The Wire", "Season 1");
    }

    [Fact]
    public async Task Searching_season_zero_is_named_Specials_on_the_lane()
    {
        var (app, pushes) = await StartAsync();
        await using var _ = app;
        var show = await ApiScenes.ShowOnDiskAsync(app, "The Wire", 2002);

        // The scene holds no specials, so the search itself is refused; the lane still names what was asked for while it ran.
        await app.Client.PostAsync($"/api/series/{show.SeriesId}/seasons/0/search", null);

        AssertOnTheLaneThenGone(pushes, "series", show.SeriesId, "The Wire", "Specials");
    }

    [Fact]
    public async Task Search_episode_puts_the_show_on_the_lane_as_that_episode_and_takes_it_off_when_the_search_ends()
    {
        var (app, pushes) = await StartAsync();
        await using var _ = app;
        var show = await ApiScenes.ShowOnDiskAsync(app, "The Wire", 2002);

        var response = await app.Client.PostAsJsonAsync($"/api/series/{show.SeriesId}/episodes/search", new { episodeIds = new[] { show.EpisodeId } });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        AssertOnTheLaneThenGone(pushes, "series", show.SeriesId, "The Wire", "S01E01");
    }

    [Fact]
    public async Task A_search_that_is_refused_before_it_starts_never_touches_the_lane()
    {
        var (app, pushes) = await StartAsync();
        await using var _ = app;
        var show = await ApiScenes.ShowOnDiskAsync(app, "The Wire", 2002);

        var response = await app.Client.PostAsJsonAsync($"/api/series/{show.SeriesId}/episodes/search", new { episodeIds = Array.Empty<string>() });

        Assert.False(response.IsSuccessStatusCode);
        Assert.Empty(Lists(pushes));
    }
}
