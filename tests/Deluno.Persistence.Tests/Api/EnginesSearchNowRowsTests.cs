using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>"Search now" on a film through the real host with no indexer set up: the record it leaves behind. Refs #1296.</summary>
public sealed class EnginesSearchNowRowsTests
{
    private static async Task<(string LibraryId, string MovieId)> WantedFilmAsync(ApplicationTestHost app)
    {
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var added = await app.Client.PostAsJsonAsync("/api/movies/", new { title = "Search Now Film", releaseYear = 2019, monitored = true, libraryId, searchForMissingNow = false });
        Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
        return (libraryId, (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);
    }

    [Fact]
    public async Task Search_now_on_a_film_records_a_manual_search_history_row_with_a_retry_time_an_activity_line_and_a_decision_record()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (libraryId, movieId) = await WantedFilmAsync(app);
        var delayHours = Math.Max(1, await ApiScenes.ScalarAsync(app, "platform", $"SELECT COALESCE(retry_delay_hours, 0) FROM libraries WHERE id = '{libraryId}'"));
        var before = DateTimeOffset.UtcNow;

        var response = await app.Client.PostAsync($"/api/movies/{movieId}/search", content: null);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Equal(1, await ApiScenes.ScalarAsync(app, "movies", $"SELECT COUNT(*) FROM movie_search_history WHERE movie_id = '{movieId}' AND trigger_kind = 'manual'"));
        var outcome = await ApiScenes.TextAsync(app, "movies", $"SELECT outcome FROM movie_search_history WHERE movie_id = '{movieId}'");
        Assert.False(string.IsNullOrWhiteSpace(outcome));
        var next = DateTimeOffset.Parse((await ApiScenes.TextAsync(app, "movies", $"SELECT next_eligible_search_utc FROM movie_wanted_state WHERE movie_id = '{movieId}'"))!);
        Assert.InRange((next - before).TotalHours, delayHours - 0.1, delayHours + 0.1);

        var activity = await app.Client.GetStringAsync("/api/activity?take=100");
        Assert.Contains("Search now was used for Search Now Film, from a screen or an outside app.", activity);
        Assert.Equal(1, await ApiScenes.ScalarAsync(app, "jobs", "SELECT COUNT(*) FROM activity_events WHERE category = 'movie.search.manual'"));
        Assert.Equal(1, await ApiScenes.ScalarAsync(app, "jobs", $"SELECT COUNT(*) FROM activity_events WHERE category = 'decision.explained' AND related_entity_id = '{movieId}' AND details_json LIKE '%\"scope\":\"movie.search\"%'"));
        Assert.Contains("\"mode\":\"manual\"", await ApiScenes.TextAsync(app, "jobs", $"SELECT details_json FROM activity_events WHERE category = 'decision.explained' AND related_entity_id = '{movieId}'"));
    }

    [Fact]
    public async Task A_retry_window_is_one_row_per_title_library_and_action_kind_and_counts_attempts()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var now = DateTimeOffset.UtcNow;

        await jobs.RecordSearchRetryWindowAsync("movie", "m1", "lib", "movies", "missing", now.AddHours(1), now, "Nothing found.", CancellationToken.None);
        await jobs.RecordSearchRetryWindowAsync("movie", "m1", "lib", "movies", "missing", now.AddHours(5), now.AddMinutes(1), "Still nothing.", CancellationToken.None);
        await jobs.RecordSearchRetryWindowAsync("movie", "m1", "lib", "movies", "upgrade", now.AddHours(2), now, "Nothing better.", CancellationToken.None);
        await jobs.RecordSearchRetryWindowAsync("movie", "m1", "other-lib", "movies", "missing", now.AddHours(2), now, "Nothing found.", CancellationToken.None);

        Assert.Equal(3, await ApiScenes.ScalarAsync(app, "jobs", "SELECT COUNT(*) FROM search_retry_windows WHERE entity_id = 'm1'"));
        Assert.Equal(2, await ApiScenes.ScalarAsync(app, "jobs", "SELECT attempt_count FROM search_retry_windows WHERE entity_id = 'm1' AND library_id = 'lib' AND action_kind = 'missing'"));
        Assert.Equal("Still nothing.", await ApiScenes.TextAsync(app, "jobs", "SELECT last_result FROM search_retry_windows WHERE entity_id = 'm1' AND library_id = 'lib' AND action_kind = 'missing'"));
        Assert.Equal(1, await ApiScenes.ScalarAsync(app, "jobs", "SELECT attempt_count FROM search_retry_windows WHERE entity_id = 'm1' AND library_id = 'lib' AND action_kind = 'upgrade'"));
        Assert.Equal(1, await ApiScenes.ScalarAsync(app, "jobs", "SELECT attempt_count FROM search_retry_windows WHERE entity_id = 'm1' AND library_id = 'other-lib'"));
    }
}
