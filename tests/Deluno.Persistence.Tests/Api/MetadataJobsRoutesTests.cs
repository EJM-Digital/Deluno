using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The three Run buttons of Settings → Metadata (#1282): "Fill in missing details" (films and shows, only what is missing) and
/// "Refresh all Movies" / "Refresh all TV shows" (everything), on the whole application with real films and shows on disk.
/// What each queues is read from the job queue file itself.
/// </summary>
public sealed class MetadataJobsRoutesTests
{
    private static async Task<JsonElement> RunAsync(ApplicationTestHost app, string kind, bool forceAll, int take = 500)
    {
        var response = await app.Client.PostAsJsonAsync($"/api/{kind}/metadata/jobs", new { forceAll, take });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<long> QueuedAsync(ApplicationTestHost app, string jobType)
        => ApiScenes.ScalarAsync(app, DelunoDatabaseNames.Jobs, $"SELECT count(*) FROM job_queue WHERE job_type = '{jobType}'");

    [Fact]
    public async Task Fill_in_missing_details_queues_a_refresh_for_each_film_and_show_with_something_missing_and_marks_nothing_else()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var library = await ApiScenes.CreateLibraryAsync(app, "movies");
        await ApiScenes.FilmOnDiskAsync(app, "Dune", 2021, libraryId: library);
        await ApiScenes.FilmOnDiskAsync(app, "Alien", 1979, libraryId: library);
        await ApiScenes.ShowOnDiskAsync(app, "The Wire", 2002);

        var films = await RunAsync(app, "movies", forceAll: false);
        var shows = await RunAsync(app, "series", forceAll: false);

        Assert.Equal(2, films.GetProperty("enqueuedCount").GetInt32());
        Assert.Equal(0, films.GetProperty("remainingCount").GetInt32());
        Assert.Equal(0, films.GetProperty("markedForRefreshCount").GetInt32());
        Assert.Equal("Queued 2 titles. That is everything that needs refreshing.", films.GetProperty("message").GetString());
        Assert.Equal(1, shows.GetProperty("enqueuedCount").GetInt32());
        Assert.Equal("Queued 1 title. That is everything that needs refreshing.", shows.GetProperty("message").GetString());
        Assert.Equal(2, await QueuedAsync(app, "movies.metadata.refresh"));
        Assert.Equal(1, await QueuedAsync(app, "series.metadata.refresh"));
    }

    [Fact]
    public async Task Refresh_all_marks_every_title_of_that_kind_and_queues_up_to_the_take_saying_how_many_are_still_to_go()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var library = await ApiScenes.CreateLibraryAsync(app, "movies");
        foreach (var (title, year) in new[] { ("Dune", 2021), ("Alien", 1979), ("Heat", 1995) })
        {
            await ApiScenes.FilmOnDiskAsync(app, title, year, libraryId: library);
        }

        var all = await RunAsync(app, "movies", forceAll: true, take: 2);

        Assert.Equal(3, all.GetProperty("markedForRefreshCount").GetInt32());
        Assert.Equal(2, all.GetProperty("enqueuedCount").GetInt32());
        Assert.Equal(1, all.GetProperty("remainingCount").GetInt32());
        Assert.Equal("Queued 2 titles. Another 1 still to go — Deluno keeps working through them in the background.", all.GetProperty("message").GetString());
        Assert.Equal(2, await QueuedAsync(app, "movies.metadata.refresh"));
        // Refreshing the films touches no show.
        Assert.Equal(0, await QueuedAsync(app, "series.metadata.refresh"));
    }

    [Fact]
    public async Task With_nothing_in_the_library_a_run_says_nothing_needs_refreshing_and_queues_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var films = await RunAsync(app, "movies", forceAll: true);
        var shows = await RunAsync(app, "series", forceAll: false);

        Assert.Equal("Nothing needs refreshing.", films.GetProperty("message").GetString());
        Assert.Equal("Nothing needs refreshing.", shows.GetProperty("message").GetString());
        Assert.Equal(0, await QueuedAsync(app, "movies.metadata.refresh") + await QueuedAsync(app, "series.metadata.refresh"));
    }
}
